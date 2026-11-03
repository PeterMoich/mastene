using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TenantManager.Application.Abstractions;

namespace TenantManager.Infrastructure.Identity;

public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "tenant-manager";
    public string Audience { get; set; } = "tenant-manager";

    /// <summary>At least 32 characters. Comes from an environment variable or user-secrets, never from git.</summary>
    public string? SigningKey { get; set; }

    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;
}

/// <summary>Claim names used in our access tokens. Kept short and un-mapped (MapInboundClaims = false).</summary>
public static class AppClaims
{
    public const string UserId = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string Role = "role";
    public const string OwnerId = "owner_id";
    public const string TenantId = "tenant_id";
}

/// <summary>
/// Holds the signing key so token creation and token validation always use the same bytes.
/// In Development, a missing key is replaced by a random one (tokens then die on restart,
/// which is fine locally). Anywhere else, a missing or short key stops the app from starting.
/// </summary>
public sealed class JwtSigningKey
{
    public JwtSigningKey(IOptions<JwtOptions> options, bool allowGenerated)
    {
        var configured = options.Value.SigningKey;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (configured.Length < 32)
                throw new InvalidOperationException("Jwt:SigningKey must be at least 32 characters.");
            Key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configured));
        }
        else if (allowGenerated)
        {
            Key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64));
            IsGenerated = true;
        }
        else
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey is not configured. Set the Jwt__SigningKey environment variable.");
        }
    }

    public SymmetricSecurityKey Key { get; }
    public bool IsGenerated { get; }
}

public sealed class TokenService(IOptions<JwtOptions> options, JwtSigningKey signingKey, IClock clock)
{
    private readonly JwtOptions _options = options.Value;
    private readonly JsonWebTokenHandler _handler = new();

    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(AppUser user, string role)
    {
        var now = clock.UtcNow;
        var expires = now.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(AppClaims.UserId, user.Id.ToString()),
            new(AppClaims.Email, user.Email ?? string.Empty),
            new(AppClaims.Name, user.DisplayName),
            new(AppClaims.Role, role),
        };

        // Landlords own their own data; tenants belong to their landlord; admins own nothing.
        var ownerId = role switch
        {
            Roles.Landlord => user.Id,
            Roles.Tenant => user.OwnerId,
            _ => null,
        };
        if (ownerId is { } owner) claims.Add(new Claim(AppClaims.OwnerId, owner.ToString()));
        if (user.TenantId is { } tenantId) claims.Add(new Claim(AppClaims.TenantId, tenantId.ToString()));

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(signingKey.Key, SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }

    /// <summary>A random 256-bit token for the client, and the hash we store.</summary>
    public static (string Token, string Hash) CreateRefreshToken()
    {
        var token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        return (token, HashRefreshToken(token));
    }

    public static string HashRefreshToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
