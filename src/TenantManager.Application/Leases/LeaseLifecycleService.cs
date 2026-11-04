using Microsoft.EntityFrameworkCore;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Payments;
using TenantManager.Application.Settings;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Tenants;

namespace TenantManager.Application.Leases;

/// <summary>What one daily run changed.</summary>
public sealed record DailyChecksSummary(
    int Activated, int Renewed, int MarkedExpiring, int NoticesRecorded, int MovedToMonthToMonth, int Ended,
    int Terminated, int ChargesRaised, int OverdueNoticesRecorded);

/// <summary>
/// Runs the daily rules for every landlord: start signed leases, swap in renewals, flag Expiring and
/// record notices (BR05), lapse to month-to-month or Ended (BR07), apply scheduled terminations, then
/// raise this month's rent charges and record overdue notices (BR08). Called by the Hangfire job and
/// by the "run checks now" endpoint.
/// </summary>
public sealed class LeaseLifecycleService(IApplicationDbContext db, IClock clock, SettingsService settings,
    RentService rent)
{
    public async Task<DailyChecksSummary> RunAsync(CancellationToken ct)
    {
        var leases = await RunLeaseRulesAsync(ct);
        var charges = await rent.RaiseChargesAsync(null, ct);
        var overdue = await rent.RecordOverdueNoticesAsync(ct);
        return new DailyChecksSummary(leases.Activated, leases.Renewed, leases.MarkedExpiring, leases.NoticesRecorded,
            leases.MovedToMonthToMonth, leases.Ended, leases.Terminated, charges, overdue);
    }

    private async Task<LifecycleSummary> RunLeaseRulesAsync(CancellationToken ct)
    {
        var inForce = LeaseTerm.InForce;
        var leases = await db.Leases.IgnoreQueryFilters()
            .Where(l => inForce.Contains(l.Status))
            .Include(l => l.Unit)
            .Include(l => l.Previous)
            .Include(l => l.Signatures)
            .Include(l => l.ExpiryNotices)
            .ToListAsync(ct);

        var summary = LeaseLifecycle.Run(leases, clock.Today(), await settings.GetLeaseSettingsAsync(ct));
        await db.SaveChangesAsync(ct);
        return summary;
    }
}

/// <summary>
/// BR19: a tenant with a lease in force (waiting for signatures, active or expiring) or an
/// outstanding rent balance can't be deleted.
/// </summary>
public sealed class LeaseTenantObligationsProvider(IApplicationDbContext db, RentService rent) : ITenantObligationsProvider
{
    public async Task<TenantObligations> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var inForce = LeaseTerm.InForce;
        var hasLease = await db.Leases.IgnoreQueryFilters()
            .AnyAsync(l => l.TenantId == tenantId && inForce.Contains(l.Status), cancellationToken);
        return new TenantObligations(hasLease, await rent.OutstandingForTenantAsync(tenantId, cancellationToken));
    }
}
