using System.Diagnostics;
using System.Text.Json;
using TenantManager.Application.Abstractions;
using TenantManager.Domain.Auditing;
using TenantManager.Infrastructure.Persistence;

namespace TenantManager.Infrastructure.Identity;

/// <summary>
/// Security events for the audit log (BR20): sign-ins, lockouts, token theft and user management.
/// Identity's tables aren't IAuditable entities, so these rows are written explicitly. Only ids, roles
/// and flags are recorded, never passwords, tokens or the emails people tried.
/// </summary>
public sealed class SecurityAudit(AppDbContext db, IClock clock, ICurrentUser currentUser)
{
    public const string EntityType = "User";

    public static class Actions
    {
        public const string SignedIn = "SignedIn";
        public const string SignInFailed = "SignInFailed";
        public const string LockedOut = "LockedOut";
        public const string TokenReuse = "RefreshTokenReuse";
        public const string UserCreated = "UserCreated";
        public const string RoleChanged = "RoleChanged";
        public const string Locked = "Locked";
        public const string Unlocked = "Unlocked";
        public const string TenantLogin = "TenantLoginCreated";
        public const string Anonymised = "Anonymised";
    }

    /// <param name="actor">Who did it; defaults to the signed-in user (null for the user themself signing in).</param>
    public async Task WriteAsync(Guid userId, string action, object? before = null, object? after = null,
        Guid? actor = null, CancellationToken ct = default)
    {
        db.AuditLogs.Add(new AuditLog(clock.UtcNow, actor ?? currentUser.UserId, EntityType, userId.ToString(), action,
            before is null ? null : JsonSerializer.Serialize(before), after is null ? null : JsonSerializer.Serialize(after),
            Activity.Current?.TraceId.ToString()));
        await db.SaveChangesAsync(ct);
    }
}
