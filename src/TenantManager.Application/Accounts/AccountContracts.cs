using FluentValidation;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Common;

namespace TenantManager.Application.Accounts;

public sealed record LoginRequest(string Email, string Password);


public sealed record TenantLoginInfo(Guid UserId, string Email, DateTimeOffset CreatedAt, bool Locked);

public sealed record MeDto(Guid Id, string Email, string DisplayName, string Role, Guid? TenantId);

/// <summary>
/// What the browser gets on sign-in or refresh. The refresh token itself is not in the body: it is set
/// as an HttpOnly cookie that scripts can't read (ADR-0019).
/// </summary>
public sealed record SessionResponse(
    string AccessToken, DateTimeOffset AccessTokenExpiresAt, DateTimeOffset RefreshTokenExpiresAt, MeDto User);

public sealed record TokenResponse(
    string AccessToken, DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken, DateTimeOffset RefreshTokenExpiresAt,
    MeDto User);

public sealed record UserDto(
    Guid Id, string Email, string DisplayName, string Role, Guid? TenantId, bool IsLocked, DateTimeOffset CreatedAt);

public sealed record UserListQuery : PagedQuery
{
    public string? Search { get; init; }
    public string? Role { get; init; }
}

/// <summary>Admin-created staff accounts. Tenant logins are created from the tenant record instead.</summary>
public sealed record CreateUserRequest(string Email, string DisplayName, string Role, string Password);

public sealed record SetRoleRequest(string Role);

public sealed record SetLockRequest(bool Locked);

public sealed record TenantLoginCreated(Guid UserId, string Email, string TemporaryPassword);

/// <summary>Login, refresh-token rotation and logout. Implemented with ASP.NET Core Identity.</summary>
public interface IAuthService
{
    /// <returns>Null when the credentials are wrong or the account is locked.</returns>
    Task<TokenResponse?> LoginAsync(LoginRequest request, CancellationToken ct);

    /// <returns>Null when the refresh token is unknown, expired or already used.</returns>
    Task<TokenResponse?> RefreshAsync(string refreshToken, CancellationToken ct);

    Task LogoutAsync(string refreshToken, CancellationToken ct);

    Task<MeDto?> GetMeAsync(Guid userId, CancellationToken ct);
}

/// <summary>User administration (Admin role) plus tenant login management (Landlord role).</summary>
public interface IUserAccountService
{
    Task<PagedResult<UserDto>> ListAsync(UserListQuery query, CancellationToken ct);
    Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken ct);
    Task<UserDto> SetRoleAsync(Guid userId, string role, CancellationToken ct);
    Task<UserDto> SetLockedAsync(Guid userId, bool locked, CancellationToken ct);

    Task<TenantLoginCreated> CreateTenantLoginAsync(Guid tenantId, Guid ownerId, string email, string displayName,
        CancellationToken ct);

    Task DisableTenantLoginAsync(Guid tenantId, CancellationToken ct);

    /// <summary>The tenant's login, for a data export. Null when they have none.</summary>
    Task<TenantLoginInfo?> FindTenantLoginAsync(Guid tenantId, CancellationToken ct);

    /// <summary>POPIA erasure: replaces the login's email and name, locks it and ends its sessions. Returns the user id.</summary>
    Task<Guid?> AnonymiseTenantLoginAsync(Guid tenantId, CancellationToken ct);
    Task<IReadOnlySet<Guid>> TenantIdsWithLoginAsync(IReadOnlyCollection<Guid> tenantIds, CancellationToken ct);
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Role).Must(r => r is Roles.Admin or Roles.Landlord)
            .WithMessage("Role must be Admin or Landlord. Tenant logins are created from the tenant's record.");
        // Identity enforces the full password policy; this gives an early, readable error.
        RuleFor(x => x.Password).NotEmpty().MinimumLength(12).MaximumLength(128);
    }
}

public sealed class SetRoleRequestValidator : AbstractValidator<SetRoleRequest>
{
    public SetRoleRequestValidator()
    {
        RuleFor(x => x.Role).Must(r => r is Roles.Admin or Roles.Landlord)
            .WithMessage("Role must be Admin or Landlord.");
    }
}
