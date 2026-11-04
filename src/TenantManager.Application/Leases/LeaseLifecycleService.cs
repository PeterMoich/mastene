using Microsoft.EntityFrameworkCore;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Settings;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Tenants;

namespace TenantManager.Application.Leases;

/// <summary>
/// Runs the daily lease rules for every landlord: start signed leases, swap in renewals, flag
/// Expiring and record notices (BR05), lapse to month-to-month or Ended (BR07), apply scheduled
/// terminations. Called by the Hangfire job and by the "run checks now" endpoint.
/// </summary>
public sealed class LeaseLifecycleService(IApplicationDbContext db, IClock clock, SettingsService settings)
{
    public async Task<LifecycleSummary> RunAsync(CancellationToken ct)
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

/// <summary>BR19: a tenant with a lease in force (waiting for signatures, active or expiring) can't be deleted.</summary>
public sealed class LeaseTenantObligationsProvider(IApplicationDbContext db) : ITenantObligationsProvider
{
    public async Task<TenantObligations> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var inForce = LeaseTerm.InForce;
        var hasLease = await db.Leases.IgnoreQueryFilters()
            .AnyAsync(l => l.TenantId == tenantId && inForce.Contains(l.Status), cancellationToken);
        return new TenantObligations(hasLease, OutstandingBalance: 0m); // balance arrives with the ledger in Phase 3
    }
}
