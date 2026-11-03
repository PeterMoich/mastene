using Microsoft.AspNetCore.Identity;

namespace TenantManager.Infrastructure.Identity;

/// <summary>A login. Staff users have no TenantId; a tenant's login links to their tenant record.</summary>
public sealed class AppUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Set only for users in the Tenant role.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>For tenant users, the landlord who owns their record. Landlords own their own data (OwnerId = Id).</summary>
    public Guid? OwnerId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A long-lived token used to get a new access token. Only its SHA-256 hash is stored, so a
/// database leak does not hand out working tokens. Each use rotates it (ADR-002).
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedByTokenHash { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}
