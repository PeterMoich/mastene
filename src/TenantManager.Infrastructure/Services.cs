using TenantManager.Application.Abstractions;
using TenantManager.Domain.Tenants;

namespace TenantManager.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// Phase 1 placeholder for BR19: leases (Phase 2) and the rent ledger (Phase 3) don't exist yet,
/// so no tenant can have an active lease or a balance. Replaced by a real query in those phases.
/// </summary>
public sealed class NoTenantObligationsProvider : ITenantObligationsProvider
{
    public Task<TenantObligations> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(TenantObligations.None);
}
