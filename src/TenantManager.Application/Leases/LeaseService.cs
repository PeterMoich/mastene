using System.Linq.Expressions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Common;
using TenantManager.Application.Settings;
using TenantManager.Domain.Common;
using TenantManager.Domain.Leases;

namespace TenantManager.Application.Leases;

/// <summary>
/// Lease use cases. Landlords manage their own leases; a tenant sees and signs only their own,
/// and never sees drafts. Anything else returns "not found" so ids can't be probed (BR01).
/// </summary>
/// <remarks>
/// Lease queries use IgnoreQueryFilters: a lease is history and must still show the names of a
/// tenant, unit or property deleted since. Ownership is filtered explicitly in <see cref="Visible"/>.
/// </remarks>
public sealed class LeaseService(
    IApplicationDbContext db,
    ICurrentUser user,
    IClock clock,
    SettingsService settings,
    LeaseDocumentFactory documents,
    IFileStorage files,
    IValidator<LeaseRequest> leaseValidator,
    IValidator<RenewLeaseRequest> renewValidator,
    IValidator<TerminateLeaseRequest> terminateValidator,
    Payments.RentService rent)
{
    private static readonly Dictionary<string, Expression<Func<Lease, object>>> Sorts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["startDate"] = l => l.StartDate,
            ["endDate"] = l => l.EndDate,
            ["leaseNumber"] = l => l.LeaseNumber,
            ["status"] = l => l.Status,
            ["monthlyRent"] = l => l.MonthlyRent,
            ["tenantName"] = l => l.Tenant.LastName,
        };

    private IQueryable<Lease> Visible() => db.VisibleLeases(user);

    public Task<PagedResult<LeaseSummaryDto>> ListAsync(LeaseListQuery query, CancellationToken ct)
    {
        var q = Visible();
        if (query.Status is { } status) q = q.Where(l => l.Status == status);
        if (query.PropertyId is { } propertyId) q = q.Where(l => l.Unit.PropertyId == propertyId);
        if (query.TenantId is { } tenantId) q = q.Where(l => l.TenantId == tenantId);
        if (query.From is { } from) q = q.Where(l => l.EndDate >= from || l.IsMonthToMonth);
        if (query.To is { } to) q = q.Where(l => l.StartDate <= to);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(l => l.LeaseNumber.Contains(term) || l.Tenant.FirstName.Contains(term) ||
                             l.Tenant.LastName.Contains(term));
        }

        return q.ApplySort(query.Sort, Sorts, "-startDate").ToPagedAsync(query, l => new LeaseSummaryDto(
            l.Id, l.LeaseNumber, l.Version, l.Status, l.IsMonthToMonth,
            l.Unit.PropertyId, l.Unit.Property.Name, l.UnitId, l.Unit.Label,
            l.TenantId, l.Tenant.FirstName + " " + l.Tenant.LastName,
            l.StartDate, l.EndDate, l.MonthlyRent, l.TerminatesOn), ct);
    }

    public async Task<LeaseDto> GetAsync(Guid id, CancellationToken ct)
    {
        var lease = await WithDetails(Visible()).AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct)
                    ?? throw new NotFoundException("Lease", id);
        var renewalId = await Visible()
            .Where(r => r.PreviousLeaseId == id && r.Status != LeaseStatus.Terminated)
            .Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
        return ToDto(lease, renewalId);
    }

    /// <summary>All versions sharing this lease's number, oldest first.</summary>
    public async Task<IReadOnlyList<LeaseSummaryDto>> VersionsAsync(Guid id, CancellationToken ct)
    {
        var number = await Visible().Where(l => l.Id == id).Select(l => l.LeaseNumber).FirstOrDefaultAsync(ct)
                     ?? throw new NotFoundException("Lease", id);
        var page = await ListAsync(new LeaseListQuery { Search = number, Sort = "startDate", PageSize = 100 }, ct);
        return page.Items.Where(l => l.LeaseNumber == number).OrderBy(l => l.Version).ToList();
    }

    public async Task<LeaseDto> CreateAsync(LeaseRequest request, CancellationToken ct)
    {
        await leaseValidator.ValidateAndThrowAsync(request, ct);
        var (unit, tenant) = await LoadPartiesAsync(request, ct);
        var leaseSettings = await settings.GetLeaseSettingsAsync(ct);
        var number = await db.NextLeaseNumberAsync(clock.Today(), ct);

        var lease = Lease.CreateDraft(number, unit, tenant, request.StartDate, request.MonthlyRent,
            request.DepositAmount, request.EscalationPct ?? leaseSettings.DefaultEscalationPct, leaseSettings);
        await EnsureNoClashAsync(lease, ct); // early warning; checked again under a lock when generated

        db.Leases.Add(lease);
        await db.SaveChangesAsync(ct);
        return await GetAsync(lease.Id, ct);
    }

    public async Task<LeaseDto> UpdateAsync(Guid id, LeaseRequest request, CancellationToken ct)
    {
        await leaseValidator.ValidateAndThrowAsync(request, ct);
        var lease = await LoadOwnedForWriteAsync(id, ct);
        lease.EnsureDraft();
        var (unit, tenant) = await LoadPartiesAsync(request, ct);
        var leaseSettings = await settings.GetLeaseSettingsAsync(ct);

        lease.UpdateDraft(unit, tenant, request.StartDate, request.MonthlyRent, request.DepositAmount,
            request.EscalationPct ?? lease.EscalationPct, leaseSettings);
        await EnsureNoClashAsync(lease, ct);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Deletes a draft. Anything already sent for signing is kept and can only be cancelled.</summary>
    public async Task DiscardAsync(Guid id, CancellationToken ct)
    {
        var lease = await LoadOwnedForWriteAsync(id, ct);
        lease.EnsureDraft();
        db.Leases.Remove(lease);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Draft → Pending signature: renders the PDF and freezes the lease (BR04). The overlap rules (BR03)
    /// are checked here under locks on the unit and the tenant, so two leases can't both pass.
    /// </summary>
    public async Task<LeaseDto> GenerateAsync(Guid id, CancellationToken ct)
    {
        var ownerId = user.RequireOwnerId();
        var keys = await db.Leases.Where(l => l.Id == id && l.OwnerId == ownerId)
                       .Select(l => new { l.UnitId, l.TenantId }).FirstOrDefaultAsync(ct)
                   ?? throw new NotFoundException("Lease", id);

        await db.InLockedTransactionAsync([$"unit:{keys.UnitId}", $"tenant:{keys.TenantId}"], async c =>
        {
            var lease = await LoadOwnedForWriteAsync(id, c);
            lease.EnsureDraft();
            if (lease.Unit.IsDeleted || lease.Unit.Property.IsDeleted || lease.Tenant.IsDeleted)
                throw new DomainRuleException("lease.party_removed",
                    "The unit or tenant on this draft has been deleted. Discard the draft.", RuleKind.Conflict);
            await EnsureNoClashAsync(lease, c);

            var leaseSettings = await settings.GetLeaseSettingsAsync(c);
            var landlordName = user.DisplayName ?? "Landlord";
            var pdf = await documents.CreateAsync(lease, LeaseDocumentKind.Unsigned, landlordName,
                leaseSettings.TemplateVersion, c);
            lease.Generate(landlordName, leaseSettings.TemplateVersion, pdf);
            await db.SaveChangesAsync(c);
            return true;
        }, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// Records the current user's signature: the landlord signs as Landlord, a tenant as Tenant.
    /// The second signature makes the signed PDF and, if the start date has come, starts the lease.
    /// </summary>
    public async Task<LeaseDto> SignAsync(Guid id, CancellationToken ct)
    {
        if (!await Visible().AnyAsync(l => l.Id == id, ct)) throw new NotFoundException("Lease", id);
        var party = user.IsTenant() ? LeaseParty.Tenant : LeaseParty.Landlord;

        await db.InLockedTransactionAsync([$"lease:{id}"], async c =>
        {
            var lease = await WithDetails(Visible()).Include(l => l.Previous)
                            .FirstOrDefaultAsync(l => l.Id == id, c)
                        ?? throw new NotFoundException("Lease", id);

            var completed = lease.Sign(party, user.RequireUserId(), user.DisplayName ?? party.ToString(),
                user.IpAddress, clock.UtcNow, clock.Today());
            if (completed)
            {
                var signedPdf = await documents.CreateAsync(lease, LeaseDocumentKind.Signed,
                    lease.LandlordName ?? "Landlord", lease.TemplateVersion ?? "unknown", c);
                lease.AddSignedDocument(signedPdf);
            }
            await db.SaveChangesAsync(c);
            return true;
        }, ct);
        try
        {
            await rent.RaiseChargesAsync([id], ct); // a lease that just started gets its rent charges now
        }
        catch (DomainRuleException ex) when (ex.Code == "duplicate")
        {
            // The daily job raised them at the same moment; nothing to do.
        }
        return await GetAsync(id, ct);
    }

    /// <summary>BR06: creates the next version as a draft, with escalated rent.</summary>
    public async Task<LeaseDto> RenewAsync(Guid id, RenewLeaseRequest request, CancellationToken ct)
    {
        await renewValidator.ValidateAndThrowAsync(request, ct);
        var lease = await LoadOwnedForWriteAsync(id, ct);
        if (await db.Leases.AnyAsync(r => r.PreviousLeaseId == id && r.Status != LeaseStatus.Terminated, ct))
            throw new DomainRuleException("lease.renewal_exists", "This lease already has a renewal.",
                RuleKind.Conflict);

        var leaseSettings = await settings.GetLeaseSettingsAsync(ct);
        var renewal = lease.CreateRenewal(request.EscalationPct ?? lease.EscalationPct, request.StartDate,
            clock.Today(), leaseSettings);
        await EnsureNoClashAsync(renewal, ct);

        db.Leases.Add(renewal);
        await db.SaveChangesAsync(ct);
        return await GetAsync(renewal.Id, ct);
    }

    /// <summary>Cancels a lease waiting for signatures, or schedules the last day of an active one.</summary>
    public async Task<LeaseDto> TerminateAsync(Guid id, TerminateLeaseRequest request, CancellationToken ct)
    {
        await terminateValidator.ValidateAndThrowAsync(request, ct);
        var lease = await LoadOwnedForWriteAsync(id, ct);
        var openRenewal = await db.Leases.AnyAsync(r => r.PreviousLeaseId == id &&
            (r.Status == LeaseStatus.Draft || r.Status == LeaseStatus.PendingSignature), ct);
        if (openRenewal && lease.IsCurrent)
            throw new DomainRuleException("lease.has_pending_renewal",
                "This lease has a renewal in progress. Discard or cancel the renewal first.", RuleKind.Conflict);

        lease.Terminate(request.Reason, request.LastDay, clock.Today(), await settings.GetLeaseSettingsAsync(ct));
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<(Stream Content, string FileName)> OpenDocumentAsync(Guid leaseId, Guid documentId,
        CancellationToken ct)
    {
        var doc = await (from l in Visible()
                         where l.Id == leaseId
                         from d in l.Documents
                         where d.Id == documentId
                         select new { d.BlobKey, d.Kind, l.LeaseNumber, l.Version })
                  .FirstOrDefaultAsync(ct)
                  ?? throw new NotFoundException("Document", documentId);

        var stream = await files.OpenReadAsync(doc.BlobKey, ct) ?? throw new NotFoundException("Document", documentId);
        var suffix = doc.Kind == LeaseDocumentKind.Signed ? "signed" : "for-signing";
        return (stream, $"{doc.LeaseNumber}-v{doc.Version}-{suffix}.pdf");
    }

    /// <summary>
    /// BR03: a unit can't have two leases in force for overlapping dates, and a tenant can't hold leases
    /// on two units in the same property at once. Drafts don't count until they are generated.
    /// </summary>
    private async Task EnsureNoClashAsync(Lease lease, CancellationToken ct)
    {
        var propertyId = lease.Unit.PropertyId;
        var previousId = lease.Previous?.Id ?? lease.PreviousLeaseId ?? Guid.Empty;
        var inForce = LeaseTerm.InForce;

        var others = await db.Leases.IgnoreQueryFilters()
            .Where(l => l.Id != lease.Id && l.Id != previousId && l.OwnerId == lease.OwnerId &&
                        inForce.Contains(l.Status) &&
                        (l.UnitId == lease.UnitId || (l.TenantId == lease.TenantId && l.Unit.PropertyId == propertyId)))
            .Select(l => new { l.UnitId, l.LeaseNumber, l.StartDate, l.EndDate, l.IsMonthToMonth, l.TerminatesOn })
            .ToListAsync(ct);

        foreach (var other in others)
        {
            var otherEnd = LeaseTerm.EffectiveEnd(other.EndDate, other.IsMonthToMonth, other.TerminatesOn);
            if (!LeaseTerm.Overlaps(lease.StartDate, lease.EndDate, other.StartDate, otherEnd)) continue;

            if (other.UnitId == lease.UnitId)
                throw new DomainRuleException("lease.overlap",
                    $"This unit already has lease {other.LeaseNumber} for overlapping dates.", RuleKind.Conflict);
            throw new DomainRuleException("lease.tenant_has_lease_in_property",
                "This tenant already has a lease on another unit in this property. End that lease first.");
        }
    }

    private async Task<(Domain.Properties.Unit Unit, Domain.Tenants.Tenant Tenant)> LoadPartiesAsync(
        LeaseRequest request, CancellationToken ct)
    {
        var ownerId = user.RequireOwnerId();
        var unit = await db.Units.Include(u => u.Property)
                       .FirstOrDefaultAsync(u => u.Id == request.UnitId && u.Property.OwnerId == ownerId, ct)
                   ?? throw new NotFoundException("Unit", request.UnitId);
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == request.TenantId && t.OwnerId == ownerId, ct)
                     ?? throw new NotFoundException("Tenant", request.TenantId);
        return (unit, tenant);
    }

    private async Task<Lease> LoadOwnedForWriteAsync(Guid id, CancellationToken ct)
    {
        var ownerId = user.RequireOwnerId();
        if (user.IsTenant()) throw new UnauthorizedAccessException();
        return await WithDetails(db.Leases.IgnoreQueryFilters())
                   .FirstOrDefaultAsync(l => l.Id == id && l.OwnerId == ownerId, ct)
               ?? throw new NotFoundException("Lease", id);
    }

    private static IQueryable<Lease> WithDetails(IQueryable<Lease> leases) => leases
        .Include(l => l.Unit).ThenInclude(u => u.Property)
        .Include(l => l.Tenant)
        .Include(l => l.Signatures)
        .Include(l => l.Documents)
        .Include(l => l.ExpiryNotices);

    private LeaseDto ToDto(Lease l, Guid? renewalId)
    {
        var p = l.Unit.Property;
        var address = string.Join(", ", new[] { p.AddressLine1, p.AddressLine2, p.City, p.Province, p.PostalCode }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        return new LeaseDto(
            l.Id, l.LeaseNumber, l.Version, l.Status, l.IsMonthToMonth,
            p.Id, p.Name, address, l.UnitId, l.Unit.Label,
            l.TenantId, l.Tenant.FullName, l.LandlordName,
            l.StartDate, l.EndDate, l.MonthlyRent, l.DepositAmount, l.EscalationPct,
            l.PreviousLeaseId, renewalId, l.ActivatedOn, l.TerminatesOn, l.TerminationReason, l.TemplateVersion,
            l.Signatures.OrderBy(s => s.SignedAt).Select(s => new LeaseSignatureDto(s.Party, s.SignerName, s.SignedAt)).ToList(),
            l.Documents.OrderBy(d => d.CreatedAt).Select(d => new LeaseDocumentDto(d.Id, d.Kind, d.CreatedAt, d.SizeBytes)).ToList(),
            l.ExpiryNotices.Select(n => n.DaysBefore).OrderDescending().ToList(),
            ActionsFor(l, renewalId is not null),
            l.CreatedAt, l.UpdatedAt);
    }

    private List<string> ActionsFor(Lease l, bool hasRenewal)
    {
        var actions = new List<string>();
        if (l.Documents.Count > 0) actions.Add("download");

        if (user.IsTenant())
        {
            if (l.Status == LeaseStatus.PendingSignature && !l.HasSigned(LeaseParty.Tenant)) actions.Add("sign");
            return actions;
        }

        switch (l.Status)
        {
            case LeaseStatus.Draft:
                if (!l.IsRenewal) actions.Add("edit");
                actions.AddRange(["discard", "generate"]);
                break;
            case LeaseStatus.PendingSignature:
                if (!l.HasSigned(LeaseParty.Landlord)) actions.Add("sign");
                actions.Add("cancel");
                break;
            case LeaseStatus.Active or LeaseStatus.Expiring when l.TerminatesOn is null && !hasRenewal:
                actions.AddRange(["renew", "terminate"]);
                break;
        }
        return actions;
    }
}
