using Microsoft.EntityFrameworkCore;
using TenantManager.Domain.Auditing;
using TenantManager.Domain.Properties;
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
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
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

/// <summary>
/// Answers "does this tenant still have a lease or owe money?" for BR19.
/// Phase 1 ships a stub that always answers no; Phase 2 and 3 replace it with real queries.
/// </summary>
public interface ITenantObligationsProvider
{
    Task<Domain.Tenants.TenantObligations> GetAsync(Guid tenantId, CancellationToken cancellationToken);
}
