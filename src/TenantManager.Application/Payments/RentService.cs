using Microsoft.EntityFrameworkCore;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Common;
using TenantManager.Application.Leases;
using TenantManager.Application.Settings;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Payments;

namespace TenantManager.Application.Payments;

/// <param name="OverdueNoticeOn">When the overdue reminder was recorded (BR08); sent to the tenant from Phase 5.</param>
public sealed record LedgerLineDto(
    string Period, decimal AmountDue, DateOnly DueDate, int DaysCharged, bool Prorated, decimal Paid,
    decimal Outstanding, ChargeState State, DateOnly? OverdueNoticeOn);

/// <param name="Balance">Charged minus paid. Negative means the tenant is in credit.</param>
/// <param name="Arrears">Unpaid amount on charges past their due date plus the grace period.</param>
public sealed record LedgerDto(
    Guid LeaseId, string LeaseNumber, int Version, decimal TotalDue, decimal TotalPaid, decimal Balance,
    decimal Arrears, decimal Credit, int GraceDays, IReadOnlyList<LedgerLineDto> Lines);

public sealed record ArrearsDto(
    Guid LeaseId, string LeaseNumber, int Version, Guid TenantId, string TenantName, string PropertyName,
    string UnitLabel, decimal Arrears, decimal Balance, string OldestOverduePeriod, int DaysOverdue);

/// <summary>
/// Rent charges and the ledger (BR08, BR09). Charges are stored; the ledger is worked out from charges
/// and verified payments each time it is read (ADR-0014).
/// </summary>
public sealed class RentService(IApplicationDbContext db, ICurrentUser user, IClock clock, SettingsService settings)
{
    private static readonly LeaseStatus[] Charging = [LeaseStatus.Active, LeaseStatus.Expiring];

    public async Task<LedgerDto> GetLedgerAsync(Guid leaseId, CancellationToken ct)
    {
        var lease = await db.VisibleLeases(user).AsNoTracking().FirstOrDefaultAsync(l => l.Id == leaseId, ct)
                    ?? throw new NotFoundException("Lease", leaseId);
        var rentSettings = await settings.GetRentSettingsAsync(ct);
        var charges = await db.RentCharges.AsNoTracking().Where(c => c.LeaseId == leaseId).ToListAsync(ct);
        var result = RentLedger.Compute(ToInputs(charges), await VerifiedPaymentsAsync([leaseId], ct),
            clock.Today(), rentSettings.GraceDays);

        var byId = charges.ToDictionary(c => c.Id);
        var lines = result.Lines.Select(l =>
        {
            var charge = byId[l.ChargeId];
            var daysInMonth = charge.BillingPeriod.DaysInMonth;
            return new LedgerLineDto(l.Period, l.AmountDue, l.DueDate, charge.DaysCharged,
                charge.DaysCharged < daysInMonth, l.Paid, l.Outstanding, l.State, charge.OverdueNoticeOn);
        }).ToList();

        return new LedgerDto(lease.Id, lease.LeaseNumber, lease.Version, result.TotalDue, result.TotalPaid,
            result.Balance, result.Arrears, result.Credit, rentSettings.GraceDays, lines);
    }

    /// <summary>Leases with arrears, most overdue first. Landlord only.</summary>
    public async Task<IReadOnlyList<ArrearsDto>> ArrearsAsync(CancellationToken ct)
    {
        var ownerId = user.RequireOwnerId();
        var today = clock.Today();
        var rentSettings = await settings.GetRentSettingsAsync(ct);
        var leases = await db.Leases.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.OwnerId == ownerId && db.RentCharges.Any(c => c.LeaseId == l.Id))
            .Select(l => new
            {
                l.Id, l.LeaseNumber, l.Version, l.TenantId,
                TenantName = l.Tenant.FirstName + " " + l.Tenant.LastName,
                PropertyName = l.Unit.Property.Name, UnitLabel = l.Unit.Label,
            })
            .ToListAsync(ct);

        var ids = leases.Select(l => l.Id).ToList();
        var charges = (await db.RentCharges.AsNoTracking().Where(c => ids.Contains(c.LeaseId)).ToListAsync(ct))
            .ToLookup(c => c.LeaseId);
        var payments = (await VerifiedPaymentsWithLeaseAsync(ids, ct)).ToLookup(p => p.LeaseId, p => p.Input);

        var rows = new List<ArrearsDto>();
        foreach (var lease in leases)
        {
            var ledger = RentLedger.Compute(ToInputs(charges[lease.Id]), payments[lease.Id], today,
                rentSettings.GraceDays);
            if (ledger.Arrears <= 0) continue;
            var oldest = ledger.Lines.First(l => l.State == ChargeState.Overdue);
            rows.Add(new ArrearsDto(lease.Id, lease.LeaseNumber, lease.Version, lease.TenantId, lease.TenantName,
                lease.PropertyName, lease.UnitLabel, ledger.Arrears, ledger.Balance, oldest.Period,
                today.DayNumber - oldest.DueDate.DayNumber));
        }
        return rows.OrderByDescending(r => r.DaysOverdue).ThenByDescending(r => r.Arrears).ToList();
    }

    /// <summary>
    /// Raises missing monthly charges for Active and Expiring leases (all of them, or the given ones).
    /// Safe to repeat: each lease and month is charged once (unique index on LeaseId + Period).
    /// </summary>
    public async Task<int> RaiseChargesAsync(IReadOnlyCollection<Guid>? leaseIds, CancellationToken ct)
    {
        var today = clock.Today();
        var rentSettings = await settings.GetRentSettingsAsync(ct);
        var q = db.Leases.IgnoreQueryFilters().Where(l => Charging.Contains(l.Status));
        if (leaseIds is not null) q = q.Where(l => leaseIds.Contains(l.Id));
        var leases = await q.ToListAsync(ct);
        var ids = leases.Select(l => l.Id).ToList();
        var existing = (await db.RentCharges.Where(c => ids.Contains(c.LeaseId))
                .Select(c => new { c.LeaseId, c.Period }).ToListAsync(ct))
            .ToLookup(c => c.LeaseId, c => c.Period);

        var raised = 0;
        foreach (var lease in leases)
        {
            var charged = existing[lease.Id].ToHashSet(StringComparer.Ordinal);
            foreach (var spec in RentSchedule.Missing(lease, today, rentSettings, charged))
            {
                db.RentCharges.Add(new RentCharge(lease.Id, spec.Period, spec.AmountDue, spec.DueDate, spec.DaysCharged));
                raised++;
            }
        }
        if (raised > 0) await db.SaveChangesAsync(ct);
        return raised;
    }

    /// <summary>
    /// BR08: records one overdue notice per charge, the first day it is overdue (past the due date plus
    /// grace). Phase 5 sends these to the tenant.
    /// </summary>
    public async Task<int> RecordOverdueNoticesAsync(CancellationToken ct)
    {
        var today = clock.Today();
        var rentSettings = await settings.GetRentSettingsAsync(ct);
        var latestDue = today.AddDays(-rentSettings.GraceDays - 1);
        var leaseIds = await db.RentCharges
            .Where(c => c.OverdueNoticeOn == null && c.DueDate <= latestDue)
            .Select(c => c.LeaseId).Distinct().ToListAsync(ct);
        if (leaseIds.Count == 0) return 0;

        var charges = (await db.RentCharges.Where(c => leaseIds.Contains(c.LeaseId)).ToListAsync(ct))
            .ToLookup(c => c.LeaseId);
        var payments = (await VerifiedPaymentsWithLeaseAsync(leaseIds, ct)).ToLookup(p => p.LeaseId, p => p.Input);

        var recorded = 0;
        foreach (var leaseId in leaseIds)
        {
            var leaseCharges = charges[leaseId].ToDictionary(c => c.Id);
            var ledger = RentLedger.Compute(ToInputs(leaseCharges.Values), payments[leaseId], today,
                rentSettings.GraceDays);
            foreach (var line in ledger.Lines.Where(l => l.State == ChargeState.Overdue))
                if (leaseCharges[line.ChargeId].RecordOverdueNotice(today)) recorded++;
        }
        if (recorded > 0) await db.SaveChangesAsync(ct);
        return recorded;
    }

    /// <summary>BR19: what the tenant owes across their leases (credit on one lease doesn't cancel debt on another).</summary>
    public async Task<decimal> OutstandingForTenantAsync(Guid tenantId, CancellationToken ct)
    {
        var leaseIds = await db.Leases.IgnoreQueryFilters().Where(l => l.TenantId == tenantId)
            .Select(l => l.Id).ToListAsync(ct);
        if (leaseIds.Count == 0) return 0m;
        var rentSettings = await settings.GetRentSettingsAsync(ct);
        var charges = (await db.RentCharges.AsNoTracking().Where(c => leaseIds.Contains(c.LeaseId)).ToListAsync(ct))
            .ToLookup(c => c.LeaseId);
        var payments = (await VerifiedPaymentsWithLeaseAsync(leaseIds, ct)).ToLookup(p => p.LeaseId, p => p.Input);
        return leaseIds.Sum(id => Math.Max(0m,
            RentLedger.Compute(ToInputs(charges[id]), payments[id], clock.Today(), rentSettings.GraceDays).Balance));
    }

    /// <summary>Unpaid charges of the given leases with what is still owing on each, for reminders.</summary>
    internal async Task<List<(RentCharge Charge, decimal Outstanding, ChargeState State)>> UnpaidChargesAsync(
        IReadOnlyCollection<Guid> leaseIds, CancellationToken ct)
    {
        if (leaseIds.Count == 0) return [];
        var rentSettings = await settings.GetRentSettingsAsync(ct);
        var charges = (await db.RentCharges.AsNoTracking().Where(c => leaseIds.Contains(c.LeaseId)).ToListAsync(ct))
            .ToLookup(c => c.LeaseId);
        var payments = (await VerifiedPaymentsWithLeaseAsync(leaseIds, ct)).ToLookup(p => p.LeaseId, p => p.Input);
        var result = new List<(RentCharge, decimal, ChargeState)>();
        foreach (var leaseId in leaseIds)
        {
            var byId = charges[leaseId].ToDictionary(c => c.Id);
            var ledger = RentLedger.Compute(ToInputs(byId.Values), payments[leaseId], clock.Today(), rentSettings.GraceDays);
            result.AddRange(ledger.Lines.Where(l => l.Outstanding > 0).Select(l => (byId[l.ChargeId], l.Outstanding, l.State)));
        }
        return result;
    }

    /// <summary>The ledger for one lease as it would be with <paramref name="extra"/> also verified (for receipts).</summary>
    internal async Task<LedgerResult> ComputeWithAsync(Guid leaseId, LedgerPaymentInput extra, CancellationToken ct)
    {
        var rentSettings = await settings.GetRentSettingsAsync(ct);
        var charges = await db.RentCharges.AsNoTracking().Where(c => c.LeaseId == leaseId).ToListAsync(ct);
        var payments = (await VerifiedPaymentsAsync([leaseId], ct)).Where(p => p.Id != extra.Id).Append(extra);
        return RentLedger.Compute(ToInputs(charges), payments, clock.Today(), rentSettings.GraceDays);
    }

    private static IEnumerable<LedgerChargeInput> ToInputs(IEnumerable<RentCharge> charges) =>
        charges.Select(c => new LedgerChargeInput(c.Id, c.Period, c.AmountDue, c.DueDate));

    private async Task<List<LedgerPaymentInput>> VerifiedPaymentsAsync(IReadOnlyCollection<Guid> leaseIds,
        CancellationToken ct) =>
        (await VerifiedPaymentsWithLeaseAsync(leaseIds, ct)).Select(p => p.Input).ToList();

    private async Task<List<(Guid LeaseId, LedgerPaymentInput Input)>> VerifiedPaymentsWithLeaseAsync(
        IReadOnlyCollection<Guid> leaseIds, CancellationToken ct)
    {
        var rows = await db.Payments.AsNoTracking()
            .Where(p => leaseIds.Contains(p.LeaseId) && p.Status == PaymentStatus.Verified)
            .Select(p => new { p.LeaseId, p.Id, p.Amount, p.Period, p.ReviewedAt })
            .ToListAsync(ct);
        return rows.Select(p => (p.LeaseId, new LedgerPaymentInput(p.Id, p.Amount, p.Period, p.ReviewedAt!.Value)))
            .ToList();
    }
}
