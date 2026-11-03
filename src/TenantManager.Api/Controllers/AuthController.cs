using Asp.Versioning;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TenantManager.Api.Infrastructure;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Accounts;

namespace TenantManager.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/auth")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.Auth)]
public sealed class AuthController(IAuthService auth, IValidator<LoginRequest> loginValidator) : ControllerBase
{
    /// <summary>Signs in with email and password. Five wrong passwords lock the account for 15 minutes.</summary>
    [HttpPost("login")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TokenResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        await loginValidator.ValidateAndThrowAsync(request, ct);
        var result = await auth.LoginAsync(request, ct);
        return result is null ? InvalidCredentials("Email or password is incorrect.") : Ok(result);
    }

    /// <summary>Swaps a refresh token for a new access token and a new refresh token. Each refresh token works once.</summary>
    [HttpPost("refresh")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TokenResponse>> Refresh(RefreshRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken)) return InvalidCredentials("Session has expired.");
        var result = await auth.RefreshAsync(request.RefreshToken, ct);
        return result is null ? InvalidCredentials("Session has expired. Please sign in again.") : Ok(result);
    }

    /// <summary>Ends the session by revoking the refresh token.</summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(RefreshRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.RefreshToken)) await auth.LogoutAsync(request.RefreshToken, ct);
        return NoContent();
    }

    private ObjectResult InvalidCredentials(string title) => Problem(
        title: title, statusCode: StatusCodes.Status401Unauthorized,
        extensions: new Dictionary<string, object?> { ["code"] = "auth.invalid" });
}

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/me")]
public sealed class MeController(IAuthService auth, ICurrentUser user) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<MeDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<MeDto>> Get(CancellationToken ct)
    {
        var me = await auth.GetMeAsync(user.RequireUserId(), ct);
        return me is null ? Unauthorized() : Ok(me);
    }
}
