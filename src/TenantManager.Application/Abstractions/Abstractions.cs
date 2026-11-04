using Microsoft.EntityFrameworkCore;
using TenantManager.Domain.Auditing;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Properties;
using TenantManager.Domain.Settings;
using TenantManager.Domain.Tenants;

namespace TenantManager.Application.Abstractions;

/// <summary>
/// The slice of the database the use cases need. Application references only the
/// EF Core abstractions package, not SQL Server, so the provider stays an Infrastructure detail.
/// </summary>
public interface IApplicationDbContext
{
    DbSet<Property> Properties { get; }
    DbSet<Unit> Units { get; }
    DbSet<Tenant> Tenants { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<Lease> Leases { get; }
    DbSet<BusinessSettings> Settings { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Next lease number, e.g. L2026-0007, from a database sequence (never reused).</summary>
    Task<string> NextLeaseNumberAsync(DateOnly today, CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="work"/> in a transaction holding exclusive locks on the given keys (e.g.
    /// "unit:{id}"), so two requests can't both pass a check-then-write rule such as "no overlapping
    /// leases" (BR03). The work may be run again on a transient database error, so it must load
    /// everything it changes itself.
    /// </summary>
    Task<T> InLockedTransactionAsync<T>(IEnumerable<string> lockKeys, Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken);
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string Landlord = "Landlord";
    public const string Tenant = "Tenant";
    public static readonly IReadOnlyList<string> All = [Admin, Landlord, Tenant];
}

/// <summary>Who is making the request, taken from the validated JWT.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    string? Role { get; }

    /// <summary>The landlord whose data this user works with (the landlord's own id, or a tenant's landlord).</summary>
    Guid? OwnerId { get; }

    /// <summary>Set only for users in the Tenant role.</summary>
    Guid? TenantId { get; }

    string? CorrelationId { get; }

    /// <summary>The user's display name, from the token.</summary>
    string? DisplayName { get; }

    /// <summary>Client IP address, recorded with signatures.</summary>
    string? IpAddress { get; }
}

public static class CurrentUserExtensions
{
    public static Guid RequireUserId(this ICurrentUser user) =>
        user.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");

    public static Guid RequireOwnerId(this ICurrentUser user) =>
        user.OwnerId ?? throw new UnauthorizedAccessException("The current user is not linked to a landlord.");

    public static bool IsTenant(this ICurrentUser user) => user.Role == Roles.Tenant;
}

/// <summary>All business rules read time from here, never DateTime.Now, so tests can freeze it.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public static class BusinessTime
{
    /// <summary>All business dates (rent due, lease start and end) are South African dates.</summary>
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg");

    public static DateOnly Today(this IClock clock) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, Zone).DateTime);
}

/// <summary>Stores documents (lease PDFs now; proof of payment and photos later). ADR-0005.</summary>
public interface IFileStorage
{
    /// <summary>Writes a new file. Keys are never reused, so an existing key is an error.</summary>
    Task SaveAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);

    /// <returns>Null when there is no file with this key.</returns>
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken);
}

/// <summary>
/// Answers "does this tenant still have a lease or owe money?" for BR19.
/// Leases are checked from Phase 2; the balance is always zero until the ledger arrives in Phase 3.
/// </summary>
public interface ITenantObligationsProvider
{
    Task<Domain.Tenants.TenantObligations> GetAsync(Guid tenantId, CancellationToken cancellationToken);
}
