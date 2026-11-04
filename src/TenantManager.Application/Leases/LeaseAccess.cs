using Microsoft.EntityFrameworkCore;
using TenantManager.Application.Abstractions;
using TenantManager.Domain.Leases;

namespace TenantManager.Application.Leases;

/// <summary>
/// One definition of "which leases may this user see" (BR01), shared by the lease, rent, payment and
/// deposit services: a landlord sees their own; a tenant sees their own except drafts.
/// </summary>
public static class LeaseAccess
{
    /// <remarks>
    /// Ignores soft-delete filters: a lease is history and must still show the names of a tenant, unit
    /// or property deleted since. Ownership is filtered explicitly here instead.
    /// </remarks>
    public static IQueryable<Lease> VisibleLeases(this IApplicationDbContext db, ICurrentUser user)
    {
        var leases = db.Leases.IgnoreQueryFilters();
        if (user.IsTenant())
        {
            var tenantId = user.TenantId ?? Guid.Empty;
            return leases.Where(l => l.TenantId == tenantId && l.Status != LeaseStatus.Draft);
        }
        var ownerId = user.RequireOwnerId();
        return leases.Where(l => l.OwnerId == ownerId);
    }

    /// <summary>Leases that have run (or are running), so rent can be charged and paid against them.</summary>
    public static bool AcceptsPayments(Lease lease) =>
        lease.ActivatedOn is not null &&
        lease.Status is LeaseStatus.Active or LeaseStatus.Expiring or LeaseStatus.Renewed or LeaseStatus.Ended
            or LeaseStatus.Terminated;
}
