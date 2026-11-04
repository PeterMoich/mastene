using System.Linq.Expressions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Common;
using TenantManager.Application.Files;
using TenantManager.Application.Leases;
using TenantManager.Application.Messaging;
using TenantManager.Application.Settings;
using TenantManager.Domain.Common;
using TenantManager.Domain.Issues;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Payments;

namespace TenantManager.Application.Issues;

/// <summary>
/// Maintenance issues (BR14, BR15). Tenants log issues on their own lease and see only those; the
/// landlord sees every issue on their units and moves them through the workflow. The rules for each
/// step live in <see cref="Issue"/>; this service loads, checks access and saves.
/// </summary>
public sealed class IssueService(
    IApplicationDbContext db,
    ICurrentUser user,
    IClock clock,
    SettingsService settings,
    UploadValidator uploads,
    IFileStorage files,
    Notifier notifier,
    MessageDispatcher dispatcher,
    IValidator<CreateIssueRequest> createValidator,
    IValidator<ResolveIssueRequest> resolveValidator,
    IValidator<ReopenIssueRequest> reopenValidator,
    IValidator<ChangePriorityRequest> priorityValidator,
    IValidator<IssueCommentRequest> commentValidator)
{
    private static readonly Dictionary<string, Expression<Func<Issue, object>>> Sorts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["openedAt"] = i => i.OpenedAt,
            ["resolutionDueAt"] = i => i.ResolutionDueAt,
            ["status"] = i => i.Status,
            ["title"] = i => i.Title,
            // Priority is stored as text, so sort by its rank rather than alphabetically.
            ["priority"] = i => i.Priority == IssuePriority.Low ? 0 : i.Priority == IssuePriority.Medium ? 1
                : i.Priority == IssuePriority.High ? 2 : 3,
        };

    private IssueActor Actor => new(user.RequireUserId(), user.DisplayName ?? (user.IsTenant() ? "Tenant" : "Landlord"),
        user.IsTenant());

    private IQueryable<Issue> Visible()
    {
        if (user.IsTenant())
        {
            var tenantId = user.TenantId ?? Guid.Empty;
            return db.Issues.Where(i => i.TenantId == tenantId);
        }
        var ownerId = user.RequireOwnerId();
        return db.Issues.Where(i => i.OwnerId == ownerId);
    }

    public async Task<PagedResult<IssueSummaryDto>> ListAsync(IssueListQuery query, CancellationToken ct)
    {
        var q = Visible();
        if (query.Status is { } status) q = q.Where(i => i.Status == status);
        if (query.Priority is { } priority) q = q.Where(i => i.Priority == priority);
        if (query.LeaseId is { } leaseId) q = q.Where(i => i.LeaseId == leaseId);
        if (query.ContractorId is { } contractorId) q = q.Where(i => i.ContractorId == contractorId);
        if (query.Escalated is { } escalated) q = q.Where(i => i.IsEscalated == escalated);
        if (query.OpenOnly == true) q = q.Where(i => i.Status != IssueStatus.Closed);
        if (query.PropertyId is { } propertyId)
            q = q.Where(i => db.Units.IgnoreQueryFilters().Any(u => u.Id == i.UnitId && u.PropertyId == propertyId));

        var page = await q.AsNoTracking().ApplySort(query.Sort, Sorts, "-openedAt")
            .ToPagedAsync(query, i => new
            {
                i.Id, i.Title, i.Category, i.Priority, i.Status, i.UnitId, i.TenantId, i.ContractorId, i.OpenedAt,
                i.ResponseDueAt, i.ResolutionDueAt, i.IsEscalated, PhotoCount = i.Photos.Count,
            }, ct);

        var names = await NamesAsync(page.Items.Select(i => (i.UnitId, i.TenantId, i.ContractorId)).ToList(), ct);
        return new PagedResult<IssueSummaryDto>(page.Items.Select(i =>
        {
            var unit = names.Units[i.UnitId];
            return new IssueSummaryDto(i.Id, i.Title, i.Category, i.Priority, i.Status, i.UnitId, unit.PropertyName,
                unit.Label, i.TenantId is { } t ? names.Tenants.GetValueOrDefault(t) : null,
                i.ContractorId is { } c ? names.Contractors.GetValueOrDefault(c)?.Name : null, i.OpenedAt,
                i.ResponseDueAt, i.ResolutionDueAt, i.IsEscalated, i.PhotoCount);
        }).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<IssueDto> GetAsync(Guid id, CancellationToken ct)
    {
        var issue = await Visible().AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct)
                    ?? throw new NotFoundException("Issue", id);

        // Children are read in separate queries rather than one joined query that would multiply rows.
        var issues = db.Issues.AsNoTracking().Where(i => i.Id == id);
        var photos = await issues.SelectMany(i => i.Photos).OrderBy(p => p.At)
            .Select(p => new IssuePhotoDto(p.Id, p.FileName, p.ContentType, p.SizeBytes, p.UploadedByName, p.At))
            .ToListAsync(ct);
        var comments = await issues.SelectMany(i => i.Comments).OrderBy(c => c.At)
            .Select(c => new IssueCommentDto(c.Id, c.AuthorName, c.ByTenant, c.Body, c.At)).ToListAsync(ct);
        var events = await issues.SelectMany(i => i.Events).OrderBy(e => e.At).ThenBy(e => e.CreatedAt)
            .Select(e => new IssueEventDto(e.Kind, e.ByName, e.At, e.Note)).ToListAsync(ct);

        var names = await NamesAsync([(issue.UnitId, issue.TenantId, issue.ContractorId)], ct);
        var unit = names.Units[issue.UnitId];
        var leaseNumber = issue.LeaseId is { } leaseId
            ? await db.Leases.IgnoreQueryFilters().Where(l => l.Id == leaseId).Select(l => l.LeaseNumber).FirstOrDefaultAsync(ct)
            : null;
        var contractor = issue.ContractorId is { } cid ? names.Contractors.GetValueOrDefault(cid) : null;
        var now = clock.UtcNow;
        var autoCloseDays = (await settings.GetSlaTargetsAsync(ct)).AutoCloseDays;

        return new IssueDto(issue.Id, issue.Title, issue.Description, issue.Category, issue.Priority, issue.Status,
            issue.UnitId, unit.PropertyName, unit.Label, issue.LeaseId, leaseNumber, issue.TenantId,
            issue.TenantId is { } t ? names.Tenants.GetValueOrDefault(t) : null, issue.ReportedByName, contractor,
            issue.OpenedAt, issue.AcknowledgedAt, issue.StartedAt, issue.ResolvedAt, issue.ClosedAt, issue.ResponseDueAt,
            issue.ResolutionDueAt, issue.ResponseMet(now), issue.ResolutionMet(now), issue.IsEscalated,
            issue.ResponseMissedAt, issue.ResolutionMissedAt, issue.ResolutionNote, issue.ReopenCount, autoCloseDays,
            photos, comments, events, ActionsFor(issue, photos.Count));
    }

    public async Task<IssueDto> CreateAsync(CreateIssueRequest request, CancellationToken ct)
    {
        await createValidator.ValidateAndThrowAsync(request, ct);
        var targets = await settings.GetSlaTargetsAsync(ct);
        Lease? lease = null;
        Guid unitId;
        Guid ownerId;

        if (user.IsTenant())
        {
            if (request.LeaseId is not { } leaseId)
                throw new DomainRuleException("leaseId.required", "Choose the lease the issue is about.");
            lease = await db.VisibleLeases(user).FirstOrDefaultAsync(l => l.Id == leaseId, ct)
                    ?? throw new NotFoundException("Lease", leaseId);
            if (!IsRunning(lease))
                throw new DomainRuleException("lease.not_running",
                    "Issues can be logged on a lease that is running.", RuleKind.Conflict);
            (unitId, ownerId) = (lease.UnitId, lease.OwnerId);
        }
        else
        {
            ownerId = user.RequireOwnerId();
            if (request.LeaseId is { } leaseId)
            {
                lease = await db.VisibleLeases(user).FirstOrDefaultAsync(l => l.Id == leaseId, ct)
                        ?? throw new NotFoundException("Lease", leaseId);
                unitId = lease.UnitId;
            }
            else
            {
                unitId = request.UnitId!.Value;
                if (!await db.Units.AnyAsync(u => u.Id == unitId && u.Property.OwnerId == ownerId, ct))
                    throw new NotFoundException("Unit", unitId);
                // A let unit's issue is shared with its tenant.
                var running = new[] { LeaseStatus.Active, LeaseStatus.Expiring };
                lease = await db.Leases.Where(l => l.UnitId == unitId && running.Contains(l.Status))
                    .OrderByDescending(l => l.StartDate).FirstOrDefaultAsync(ct);
            }
        }

        var issue = new Issue(ownerId, unitId, lease?.Id, lease?.TenantId, request.Category, request.Priority,
            request.Title, request.Description, Actor, targets, clock.UtcNow);
        db.Issues.Add(issue);
        await db.SaveChangesAsync(ct);
        AppTelemetry.IssuesOpened.Add(1, new KeyValuePair<string, object?>("priority", issue.Priority.ToString()));
        return await GetAsync(issue.Id, ct);
    }

    public Task<IssueDto> AcknowledgeAsync(Guid id, CancellationToken ct) =>
        ChangeAsync(id, i => i.Acknowledge(Actor, clock.UtcNow), ct,
            i => ($"issue-acknowledged:{i.Id}", "we have seen it and will be in touch about the next steps."));

    public Task<IssueDto> StartAsync(Guid id, CancellationToken ct) =>
        ChangeAsync(id, i => i.Start(Actor, clock.UtcNow), ct,
            i => ($"issue-started:{i.Id}", "work has started."));

    public async Task<IssueDto> ResolveAsync(Guid id, ResolveIssueRequest request, CancellationToken ct)
    {
        await resolveValidator.ValidateAndThrowAsync(request, ct);
        return await ChangeAsync(id, i => i.Resolve(request.Note, Actor, clock.UtcNow), ct,
            i => ($"issue-resolved:{i.Id}:{i.ReopenCount}",
                "it has been marked as fixed. Please confirm in the Tenant Manager app, or reopen it if it isn't."));
    }

    public Task<IssueDto> CloseAsync(Guid id, CancellationToken ct) =>
        ChangeAsync(id, i => i.Close(Actor, clock.UtcNow), ct);

    public async Task<IssueDto> ReopenAsync(Guid id, ReopenIssueRequest request, CancellationToken ct)
    {
        await reopenValidator.ValidateAndThrowAsync(request, ct);
        return await ChangeAsync(id, i => i.Reopen(request.Reason, Actor, clock.UtcNow), ct);
    }

    public async Task<IssueDto> ChangePriorityAsync(Guid id, ChangePriorityRequest request, CancellationToken ct)
    {
        await priorityValidator.ValidateAndThrowAsync(request, ct);
        var targets = await settings.GetSlaTargetsAsync(ct);
        return await ChangeAsync(id, i => i.ChangePriority(request.Priority, Actor, targets, clock.UtcNow), ct);
    }

    public async Task<IssueDto> AssignContractorAsync(Guid id, AssignContractorRequest request, CancellationToken ct)
    {
        if (user.IsTenant()) throw new UnauthorizedAccessException();
        string? name = null;
        if (request.ContractorId is { } contractorId)
        {
            var ownerId = user.RequireOwnerId();
            name = await db.Contractors.Where(c => c.Id == contractorId && c.OwnerId == ownerId)
                       .Select(c => c.Name).FirstOrDefaultAsync(ct)
                   ?? throw new NotFoundException("Contractor", contractorId);
        }
        return await ChangeAsync(id, i => i.AssignContractor(request.ContractorId, name, Actor, clock.UtcNow), ct);
    }

    public async Task<IssueDto> AddCommentAsync(Guid id, IssueCommentRequest request, CancellationToken ct)
    {
        await commentValidator.ValidateAndThrowAsync(request, ct);
        return await ChangeAsync(id, i => i.AddComment(request.Body, Actor, clock.UtcNow), ct);
    }

    /// <summary>
    /// BR12: JPG or PNG up to 5 MB, at most five per issue. The count is checked again under a lock so two
    /// uploads at once can't make six.
    /// </summary>
    public async Task<IssueDto> AddPhotoAsync(Guid id, UploadedFile file, CancellationToken ct)
    {
        var count = await Visible().Where(i => i.Id == id).Select(i => (int?)i.Photos.Count).FirstOrDefaultAsync(ct)
                    ?? throw new NotFoundException("Issue", id);
        if (count >= Issue.MaxPhotos)
            throw new DomainRuleException("issue.too_many_photos", $"An issue can have at most {Issue.MaxPhotos} photos.",
                RuleKind.Conflict);

        var upload = await uploads.ValidateAsync(file, UploadRules.IssuePhoto, ct);
        var key = $"issues/{id:N}/{Guid.NewGuid():N}{upload.Extension}";
        using (var stream = new MemoryStream(upload.Content, writable: false))
        {
            await files.SaveAsync(key, stream, upload.ContentType, ct);
        }
        var stored = new StoredFile(key, upload.SafeFileName, upload.ContentType, upload.Content.LongLength, upload.Sha256);

        await db.InLockedTransactionAsync([$"issue:{id}"], async c =>
        {
            var issue = await Visible().Include(i => i.Photos).FirstAsync(i => i.Id == id, c);
            issue.AddPhoto(stored, Actor, clock.UtcNow);
            await db.SaveChangesAsync(c);
            return true;
        }, ct);
        return await GetAsync(id, ct);
    }

    public async Task<(Stream Content, string ContentType)> OpenPhotoAsync(Guid id, Guid photoId, CancellationToken ct)
    {
        var photo = await Visible().Where(i => i.Id == id).SelectMany(i => i.Photos).Where(p => p.Id == photoId)
                        .Select(p => new { p.FileKey, p.ContentType }).FirstOrDefaultAsync(ct)
                    ?? throw new NotFoundException("Photo", photoId);
        var stream = await files.OpenReadAsync(photo.FileKey, ct) ?? throw new NotFoundException("Photo", photoId);
        return (stream, photo.ContentType);
    }

    /// <summary>How each priority has done against its targets, for issues opened in the last <paramref name="days"/> days.</summary>
    public async Task<SlaReportDto> SlaReportAsync(int days, CancellationToken ct)
    {
        if (user.IsTenant()) throw new UnauthorizedAccessException();
        days = Math.Clamp(days, 1, 366);
        var now = clock.UtcNow;
        var from = now.AddDays(-days);
        var targets = await settings.GetSlaTargetsAsync(ct);
        var rows = await Visible().AsNoTracking().Where(i => i.OpenedAt >= from)
            .Select(i => new
            {
                i.Priority, i.Status, i.OpenedAt, i.AcknowledgedAt, i.ResolvedAt, i.ResponseDueAt, i.ResolutionDueAt,
                i.IsEscalated,
            })
            .ToListAsync(ct);

        var priorities = Enum.GetValues<IssuePriority>().OrderDescending().Select(p =>
        {
            var set = rows.Where(r => r.Priority == p).ToList();
            var acked = set.Where(r => r.AcknowledgedAt is not null).ToList();
            var resolved = set.Where(r => r.ResolvedAt is not null).ToList();
            var target = targets.For(p);
            return new SlaPriorityDto(p, target.ResponseHours, target.ResolutionHours, set.Count, acked.Count,
                acked.Count(r => r.AcknowledgedAt <= r.ResponseDueAt), resolved.Count,
                resolved.Count(r => r.ResolvedAt <= r.ResolutionDueAt),
                acked.Count == 0 ? null : Math.Round(acked.Average(r => (r.AcknowledgedAt!.Value - r.OpenedAt).TotalHours), 1),
                resolved.Count == 0 ? null : Math.Round(resolved.Average(r => (r.ResolvedAt!.Value - r.OpenedAt).TotalHours), 1),
                set.Count(r => r.IsEscalated), set.Count(r => r.Status != IssueStatus.Closed && r.Status != IssueStatus.Resolved));
        }).ToList();

        var today = clock.Today();
        return new SlaReportDto(today.AddDays(-days), today, priorities);
    }

    /// <param name="update">For steps the tenant hears about on WhatsApp: the dedupe key and the update text.</param>
    private async Task<IssueDto> ChangeAsync(Guid id, Action<Issue> change, CancellationToken ct,
        Func<Issue, (string Key, string Text)>? update = null)
    {
        var issue = await Visible().FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Issue", id);
        change(issue);
        if (update is not null && issue.TenantId is { } tenantId)
        {
            var (key, text) = update(issue);
            await notifier.QueueAsync(tenantId, Domain.Messaging.MessageTemplates.IssueUpdate,
                t => [t.FirstName, issue.Title, text], key, ct);
        }
        await db.SaveChangesAsync(ct);
        if (update is not null) await dispatcher.TryDispatchAsync(ct);
        return await GetAsync(id, ct);
    }

    private static bool IsRunning(Lease lease) => lease.Status is LeaseStatus.Active or LeaseStatus.Expiring;

    private List<string> ActionsFor(Issue i, int photoCount)
    {
        var actions = new List<string>();
        var tenant = user.IsTenant();
        if (!tenant)
        {
            if (i.Status == IssueStatus.Open) actions.Add("acknowledge");
            if (i.Status is IssueStatus.Open or IssueStatus.Acknowledged) actions.Add("start");
            if (i.Status == IssueStatus.InProgress) actions.Add("resolve");
            if (i.Status is not (IssueStatus.Resolved or IssueStatus.Closed)) actions.Add("changePriority");
            if (i.Status != IssueStatus.Closed) actions.Add("assignContractor");
        }
        // The tenant confirms or reopens their own issue; the landlord does so only when there is no tenant.
        if (i.Status == IssueStatus.Resolved && tenant == (i.TenantId is not null)) actions.AddRange(["close", "reopen"]);
        if (i.Status != IssueStatus.Closed)
        {
            actions.Add("comment");
            if (photoCount < Issue.MaxPhotos) actions.Add("addPhoto");
        }
        return actions;
    }

    private sealed record UnitName(string Label, string PropertyName);

    private sealed record Names(
        Dictionary<Guid, UnitName> Units, Dictionary<Guid, string> Tenants, Dictionary<Guid, ContractorRefDto> Contractors);

    /// <summary>Names for display, including soft-deleted units, tenants and contractors (an issue is history).</summary>
    private async Task<Names> NamesAsync(IReadOnlyCollection<(Guid UnitId, Guid? TenantId, Guid? ContractorId)> refs,
        CancellationToken ct)
    {
        var unitIds = refs.Select(r => r.UnitId).Distinct().ToList();
        var tenantIds = refs.Where(r => r.TenantId is not null).Select(r => r.TenantId!.Value).Distinct().ToList();
        var contractorIds = refs.Where(r => r.ContractorId is not null).Select(r => r.ContractorId!.Value).Distinct().ToList();

        var units = await db.Units.IgnoreQueryFilters().AsNoTracking().Where(u => unitIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Label, PropertyName = u.Property.Name })
            .ToDictionaryAsync(u => u.Id, u => new UnitName(u.Label, u.PropertyName), ct);
        var tenants = await db.Tenants.IgnoreQueryFilters().AsNoTracking().Where(t => tenantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.FirstName + " " + t.LastName, ct);
        var contractors = await db.Contractors.IgnoreQueryFilters().AsNoTracking().Where(c => contractorIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => new ContractorRefDto(c.Id, c.Name, c.Trade, c.Phone), ct);
        return new Names(units, tenants, contractors);
    }
}

/// <summary>
/// BR15: flags issues that missed a target and closes resolved issues the tenant didn't reply to.
/// Run every 15 minutes by Hangfire, and as part of the "run checks now" endpoint.
/// </summary>
public sealed class IssueSlaService(IApplicationDbContext db, IClock clock, SettingsService settings)
{
    public async Task<IssueChecksSummary> RunAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var autoCloseDays = (await settings.GetSlaTargetsAsync(ct)).AutoCloseDays;
        var closeBefore = now.AddDays(-autoCloseDays);

        var due = await db.Issues
            .Where(i => i.Status != IssueStatus.Closed &&
                        ((i.ResponseMissedAt == null && i.AcknowledgedAt == null && i.ResponseDueAt < now) ||
                         (i.ResolutionMissedAt == null && i.ResolvedAt == null && i.ResolutionDueAt < now) ||
                         (i.Status == IssueStatus.Resolved && i.ResolvedAt <= closeBefore)))
            .ToListAsync(ct);

        var escalated = 0;
        var closed = 0;
        foreach (var issue in due)
        {
            if (issue.CheckTargets(now))
            {
                escalated++;
                AppTelemetry.IssuesEscalated.Add(1, new KeyValuePair<string, object?>("priority", issue.Priority.ToString()));
            }
            if (issue.AutoClose(now, autoCloseDays)) closed++;
        }
        await db.SaveChangesAsync(ct);
        return new IssueChecksSummary(escalated, closed);
    }
}
