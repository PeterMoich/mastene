using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Common;
using TenantManager.Application.Files;
using TenantManager.Application.Leases;
using TenantManager.Domain.Common;
using TenantManager.Domain.Inspections;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Payments;

namespace TenantManager.Application.Inspections;

public sealed record StartInspectionRequest(InspectionKind Kind);

public sealed record InspectionDetailsRequest(DateOnly InspectedOn, string? GeneralNotes);

public sealed record InspectionAreaRequest(string Area);

public sealed record InspectionItemRequest(ItemCondition? Condition, string? Notes);

public sealed record AcknowledgeInspectionRequest(string? Comment);

public sealed record InspectionSummaryDto(
    Guid Id, InspectionKind Kind, InspectionStatus Status, DateOnly InspectedOn, DateTimeOffset? CompletedAt,
    DateTimeOffset? TenantAcknowledgedAt, int Areas, int AreasChecked, int Photos);

/// <param name="AllowedActions">startMoveIn, startMoveOut — for the current user.</param>
public sealed record LeaseInspectionsDto(string LeaseNumber, IReadOnlyList<InspectionSummaryDto> Items,
    IReadOnlyList<string> AllowedActions);

public sealed record InspectionPhotoDto(Guid Id, string FileName, string ContentType, long SizeBytes, DateTimeOffset At);

/// <param name="MoveInCondition">On a move-out inspection: what the same area was like at move-in.</param>
public sealed record InspectionItemDto(
    Guid Id, string Area, int Order, ItemCondition? Condition, string? Notes, IReadOnlyList<InspectionPhotoDto> Photos,
    ItemCondition? MoveInCondition, string? MoveInNotes, IReadOnlyList<InspectionPhotoDto> MoveInPhotos);

/// <param name="AllowedActions">edit, complete, acknowledge — for the current user.</param>
public sealed record InspectionDto(
    Guid Id, Guid LeaseId, string LeaseNumber, InspectionKind Kind, InspectionStatus Status, DateOnly InspectedOn,
    string? GeneralNotes, DateTimeOffset? CompletedAt, string? CompletedByName, DateTimeOffset? TenantAcknowledgedAt,
    string? TenantComment, string PropertyName, string UnitLabel, string TenantName, Guid? MoveInInspectionId,
    IReadOnlyList<InspectionItemDto> Items, IReadOnlyList<string> AllowedActions);

public sealed class InspectionDetailsRequestValidator : AbstractValidator<InspectionDetailsRequest>
{
    public InspectionDetailsRequestValidator()
    {
        RuleFor(x => x.InspectedOn).InclusiveBetween(new DateOnly(2000, 1, 1), new DateOnly(2099, 12, 31));
        RuleFor(x => x.GeneralNotes).MaximumLength(Inspection.NotesMax);
    }
}

public sealed class InspectionAreaRequestValidator : AbstractValidator<InspectionAreaRequest>
{
    public InspectionAreaRequestValidator() => RuleFor(x => x.Area).NotEmpty().MaximumLength(Inspection.AreaMax);
}

public sealed class InspectionItemRequestValidator : AbstractValidator<InspectionItemRequest>
{
    public InspectionItemRequestValidator()
    {
        RuleFor(x => x.Condition).IsInEnum();
        RuleFor(x => x.Notes).MaximumLength(Inspection.NotesMax);
    }
}

public sealed class AcknowledgeInspectionRequestValidator : AbstractValidator<AcknowledgeInspectionRequest>
{
    public AcknowledgeInspectionRequestValidator() => RuleFor(x => x.Comment).MaximumLength(Inspection.CommentMax);
}

/// <summary>
/// Move-in and move-out inspections with photos. The landlord fills them in and completes them; the
/// tenant sees completed ones for their lease and acknowledges them.
/// </summary>
public sealed class InspectionService(
    IApplicationDbContext db,
    ICurrentUser user,
    IClock clock,
    UploadValidator uploads,
    IFileStorage files,
    IValidator<InspectionDetailsRequest> detailsValidator,
    IValidator<InspectionAreaRequest> areaValidator,
    IValidator<InspectionItemRequest> itemValidator,
    IValidator<AcknowledgeInspectionRequest> acknowledgeValidator)
{
    private IQueryable<Inspection> Visible()
    {
        if (user.IsTenant())
        {
            var tenantId = user.TenantId ?? Guid.Empty;
            // A tenant sees an inspection once it is completed, not the landlord's draft.
            return db.Inspections.Where(i => i.TenantId == tenantId && i.Status == InspectionStatus.Completed);
        }
        var ownerId = user.RequireOwnerId();
        return db.Inspections.Where(i => i.OwnerId == ownerId);
    }

    public async Task<LeaseInspectionsDto> ForLeaseAsync(Guid leaseId, CancellationToken ct)
    {
        var lease = await db.VisibleLeases(user).AsNoTracking().FirstOrDefaultAsync(l => l.Id == leaseId, ct)
                    ?? throw new NotFoundException("Lease", leaseId);
        var items = await Visible().AsNoTracking().Where(i => i.LeaseNumber == lease.LeaseNumber)
            .OrderBy(i => i.Kind)
            .Select(i => new InspectionSummaryDto(i.Id, i.Kind, i.Status, i.InspectedOn, i.CompletedAt,
                i.TenantAcknowledgedAt, i.Items.Count, i.Items.Count(x => x.Condition != null),
                i.Items.SelectMany(x => x.Photos).Count()))
            .ToListAsync(ct);

        var actions = new List<string>();
        if (!user.IsTenant())
        {
            if (items.All(i => i.Kind != InspectionKind.MoveIn) && LeaseTerm.InForce.Contains(lease.Status))
                actions.Add("startMoveIn");
            if (items.All(i => i.Kind != InspectionKind.MoveOut) && lease.ActivatedOn is not null &&
                lease.Status is LeaseStatus.Active or LeaseStatus.Expiring
                    or LeaseStatus.Ended or LeaseStatus.Terminated)
                actions.Add("startMoveOut");
        }
        return new LeaseInspectionsDto(lease.LeaseNumber, items, actions);
    }

    public async Task<InspectionDto> StartAsync(Guid leaseId, StartInspectionRequest request, CancellationToken ct)
    {
        if (user.IsTenant()) throw new UnauthorizedAccessException();
        var lease = await db.VisibleLeases(user).FirstOrDefaultAsync(l => l.Id == leaseId, ct)
                    ?? throw new NotFoundException("Lease", leaseId);
        var existing = await db.Inspections.Where(i => i.LeaseNumber == lease.LeaseNumber).ToListAsync(ct);
        if (existing.Any(i => i.Kind == request.Kind))
            throw new DomainRuleException("inspection.exists",
                $"This lease already has a {(request.Kind == InspectionKind.MoveIn ? "move-in" : "move-out")} inspection.",
                RuleKind.Conflict);

        Inspection inspection;
        if (request.Kind == InspectionKind.MoveIn)
        {
            inspection = Inspection.StartMoveIn(lease, clock.Today());
        }
        else
        {
            var moveInId = existing.FirstOrDefault(i => i.Kind == InspectionKind.MoveIn)?.Id;
            var moveIn = moveInId is null ? null
                : await db.Inspections.Include(i => i.Items).AsNoTracking().FirstAsync(i => i.Id == moveInId, ct);
            inspection = Inspection.StartMoveOut(lease, moveIn, clock.Today());
        }
        db.Inspections.Add(inspection);
        await db.SaveChangesAsync(ct); // a unique index on (LeaseNumber, Kind) catches two starts at once
        return await GetAsync(inspection.Id, ct);
    }

    public async Task<InspectionDto> GetAsync(Guid id, CancellationToken ct)
    {
        var inspection = await Visible().AsNoTracking().Include(i => i.Items).ThenInclude(x => x.Photos)
                             .FirstOrDefaultAsync(i => i.Id == id, ct)
                         ?? throw new NotFoundException("Inspection", id);

        Inspection? moveIn = null;
        if (inspection.Kind == InspectionKind.MoveOut)
            moveIn = await db.Inspections.AsNoTracking().Include(i => i.Items).ThenInclude(x => x.Photos)
                .FirstOrDefaultAsync(i => i.LeaseNumber == inspection.LeaseNumber && i.Kind == InspectionKind.MoveIn &&
                                          i.Status == InspectionStatus.Completed, ct);
        var before = moveIn?.Items.ToDictionary(x => x.Area, StringComparer.OrdinalIgnoreCase)
                     ?? new Dictionary<string, InspectionItem>(StringComparer.OrdinalIgnoreCase);

        var names = await db.Leases.IgnoreQueryFilters().AsNoTracking().Where(l => l.Id == inspection.LeaseId)
            .Select(l => new { PropertyName = l.Unit.Property.Name, UnitLabel = l.Unit.Label,
                TenantName = l.Tenant.FirstName + " " + l.Tenant.LastName })
            .FirstAsync(ct);

        var items = inspection.Items.OrderBy(x => x.Order).Select(x =>
        {
            before.TryGetValue(x.Area, out var b);
            return new InspectionItemDto(x.Id, x.Area, x.Order, x.Condition, x.Notes, Photos(x), b?.Condition, b?.Notes,
                b is null ? [] : Photos(b));
        }).ToList();

        var actions = new List<string>();
        if (!user.IsTenant() && inspection.Status == InspectionStatus.Draft) actions.AddRange(["edit", "complete"]);
        if (user.IsTenant() && inspection.Status == InspectionStatus.Completed && inspection.TenantAcknowledgedAt is null)
            actions.Add("acknowledge");

        return new InspectionDto(inspection.Id, inspection.LeaseId, inspection.LeaseNumber, inspection.Kind,
            inspection.Status, inspection.InspectedOn, inspection.GeneralNotes, inspection.CompletedAt,
            inspection.CompletedByName, inspection.TenantAcknowledgedAt, inspection.TenantComment, names.PropertyName,
            names.UnitLabel, names.TenantName, moveIn?.Id, items, actions);
    }

    public async Task<InspectionDto> UpdateDetailsAsync(Guid id, InspectionDetailsRequest request, CancellationToken ct)
    {
        await detailsValidator.ValidateAndThrowAsync(request, ct);
        return await ChangeAsync(id, i => i.UpdateDetails(request.InspectedOn, request.GeneralNotes, clock.Today()), ct);
    }

    public async Task<InspectionDto> AddItemAsync(Guid id, InspectionAreaRequest request, CancellationToken ct)
    {
        await areaValidator.ValidateAndThrowAsync(request, ct);
        return await ChangeAsync(id, i => i.AddItem(request.Area), ct);
    }

    public async Task<InspectionDto> UpdateItemAsync(Guid id, Guid itemId, InspectionItemRequest request, CancellationToken ct)
    {
        await itemValidator.ValidateAndThrowAsync(request, ct);
        return await ChangeAsync(id, i => i.UpdateItem(itemId, request.Condition, request.Notes), ct);
    }

    public Task<InspectionDto> RemoveItemAsync(Guid id, Guid itemId, CancellationToken ct) =>
        ChangeAsync(id, i => i.RemoveItem(itemId), ct);

    public Task<InspectionDto> CompleteAsync(Guid id, CancellationToken ct) =>
        ChangeAsync(id, i => i.Complete(user.DisplayName ?? "Landlord", clock.UtcNow), ct);

    public async Task<InspectionDto> AcknowledgeAsync(Guid id, AcknowledgeInspectionRequest request, CancellationToken ct)
    {
        await acknowledgeValidator.ValidateAndThrowAsync(request, ct);
        if (!user.IsTenant()) throw new UnauthorizedAccessException("The tenant acknowledges the inspection.");
        var inspection = await Visible().FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Inspection", id);
        inspection.Acknowledge(request.Comment, clock.UtcNow);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>JPG or PNG up to 5 MB, at most five per area (checked again under a lock).</summary>
    public async Task<InspectionDto> AddPhotoAsync(Guid id, Guid itemId, UploadedFile file, CancellationToken ct)
    {
        if (user.IsTenant()) throw new UnauthorizedAccessException();
        if (!await Visible().AnyAsync(i => i.Id == id && i.Items.Any(x => x.Id == itemId), ct))
            throw new NotFoundException("Inspection item", itemId);

        var upload = await uploads.ValidateAsync(file, UploadRules.IssuePhoto, ct);
        var key = $"inspections/{id:N}/{Guid.NewGuid():N}{upload.Extension}";
        using (var stream = new MemoryStream(upload.Content, writable: false))
        {
            await files.SaveAsync(key, stream, upload.ContentType, ct);
        }
        var stored = new StoredFile(key, upload.SafeFileName, upload.ContentType, upload.Content.LongLength, upload.Sha256);

        await db.InLockedTransactionAsync([$"inspection:{id}"], async c =>
        {
            var inspection = await Visible().Include(i => i.Items).ThenInclude(x => x.Photos).FirstAsync(i => i.Id == id, c);
            inspection.AddPhoto(itemId, stored, clock.UtcNow);
            await db.SaveChangesAsync(c);
            return true;
        }, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// A photo from this inspection, or (on a move-out) from the move-in inspection it is compared with.
    /// </summary>
    public async Task<(Stream Content, string ContentType)> OpenPhotoAsync(Guid id, Guid photoId, CancellationToken ct)
    {
        var inspection = await Visible().AsNoTracking().Where(i => i.Id == id)
                             .Select(i => new { i.LeaseNumber }).FirstOrDefaultAsync(ct)
                         ?? throw new NotFoundException("Inspection", id);
        var photo = await Visible().Where(i => i.LeaseNumber == inspection.LeaseNumber)
                        .SelectMany(i => i.Items).SelectMany(x => x.Photos).Where(p => p.Id == photoId)
                        .Select(p => new { p.FileKey, p.ContentType }).FirstOrDefaultAsync(ct)
                    ?? throw new NotFoundException("Photo", photoId);
        var stream = await files.OpenReadAsync(photo.FileKey, ct) ?? throw new NotFoundException("Photo", photoId);
        return (stream, photo.ContentType);
    }

    private async Task<InspectionDto> ChangeAsync(Guid id, Action<Inspection> change, CancellationToken ct)
    {
        if (user.IsTenant()) throw new UnauthorizedAccessException();
        var inspection = await Visible().Include(i => i.Items).FirstOrDefaultAsync(i => i.Id == id, ct)
                         ?? throw new NotFoundException("Inspection", id);
        change(inspection);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    private static List<InspectionPhotoDto> Photos(InspectionItem item) =>
        item.Photos.OrderBy(p => p.At).Select(p => new InspectionPhotoDto(p.Id, p.FileName, p.ContentType, p.SizeBytes, p.At))
            .ToList();
}
