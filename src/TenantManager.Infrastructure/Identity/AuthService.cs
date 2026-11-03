using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Accounts;
using TenantManager.Infrastructure.Persistence;

namespace TenantManager.Infrastructure.Identity;

public sealed class AuthService(
    UserManager<AppUser> users,
    AppDbContext db,
    TokenService tokens,
    IOptions<JwtOptions> jwtOptions,
    IClock clock,
    ILogger<AuthService> logger) : IAuthService
{
    public async Task<TokenResponse?> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            // Same response as a wrong password, so the endpoint can't be used to find valid emails.
            return null;
        }

        if (await users.IsLockedOutAsync(user))
        {
            logger.LogWarning("Login refused for locked-out user {UserId}", user.Id);
            return null;
        }

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user); // locks the account after repeated failures
            return null;
        }

        await users.ResetAccessFailedCountAsync(user);
        return await IssueAsync(user, ct);
    }

    public async Task<TokenResponse?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var hash = TokenService.HashRefreshToken(refreshToken);
        var stored = await db.RefreshTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        var now = clock.UtcNow;
        if (stored is null) return null;

        if (stored.RevokedAt is not null)
        {
            // A rotated token was presented again: someone may have stolen it. Revoke the
            // whole family so both the thief and the real user have to sign in again.
            logger.LogWarning("Refresh token reuse detected for user {UserId}; revoking all sessions", stored.UserId);
            await RevokeAllAsync(stored.UserId, now, ct);
            return null;
        }

        if (stored.ExpiresAt <= now || await users.IsLockedOutAsync(stored.User)) return null;

        var response = await IssueAsync(stored.User, ct, rotating: stored);
        return response;
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct)
    {
        var hash = TokenService.HashRefreshToken(refreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is { RevokedAt: null })
        {
            stored.RevokedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<MeDto?> GetMeAsync(Guid userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return null;
        var role = (await users.GetRolesAsync(user)).FirstOrDefault() ?? string.Empty;
        return ToMe(user, role);
    }

    private async Task<TokenResponse> IssueAsync(AppUser user, CancellationToken ct, RefreshToken? rotating = null)
    {
        var role = (await users.GetRolesAsync(user)).FirstOrDefault()
                   ?? throw new InvalidOperationException($"User {user.Id} has no role.");
        var now = clock.UtcNow;
        var (access, accessExpires) = tokens.CreateAccessToken(user, role);
        var (refresh, refreshHash) = TokenService.CreateRefreshToken();
        var refreshExpires = now.AddDays(jwtOptions.Value.RefreshTokenDays);

        if (rotating is not null)
        {
            rotating.RevokedAt = now;
            rotating.ReplacedByTokenHash = refreshHash;
        }

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = refreshHash,
            CreatedAt = now,
            ExpiresAt = refreshExpires,
        });
        await db.SaveChangesAsync(ct);

        return new TokenResponse(access, accessExpires, refresh, refreshExpires, ToMe(user, role));
    }

    internal async Task RevokeAllAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    internal static MeDto ToMe(AppUser user, string role) =>
        new(user.Id, user.Email ?? string.Empty, user.DisplayName, role, user.TenantId);
}
