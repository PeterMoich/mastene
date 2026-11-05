using Asp.Versioning;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using TenantManager.Api.Infrastructure;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Accounts;
using TenantManager.Infrastructure.Persistence;

namespace TenantManager.Api.Controllers;

/// <summary>
/// Sign-in. The access token (15 minutes) is returned in the body and kept in memory by the app. The
/// refresh token (7 days, rotating) is an HttpOnly, SameSite=Strict cookie scoped to /api/v1/auth, so
/// scripts on the page, including injected ones, can't read it (ADR-0019).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/auth")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.Auth)]
public sealed class AuthController(IAuthService auth, IValidator<LoginRequest> loginValidator, IConfiguration configuration)
    : ControllerBase
{
    public const string RefreshCookie = "tm_refresh";
    public const string CookiePath = "/api/v1/auth";

    /// <summary>
    /// Requests that use the cookie must carry this header. A plain cross-site form can't add headers,
    /// and a cross-site script would need a CORS preflight, which the API never allows.
    /// </summary>
    public const string CsrfHeader = "X-Requested-With";

    /// <summary>Signs in with email and password. Five wrong passwords lock the account for 15 minutes.</summary>
    [HttpPost("login")]
    [ProducesResponseType<SessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SessionResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        await loginValidator.ValidateAndThrowAsync(request, ct);
        var result = await auth.LoginAsync(request, ct);
        return result is null ? InvalidCredentials("Email or password is incorrect.") : Session(result);
    }

    /// <summary>Swaps the refresh cookie for a new access token and a new refresh cookie. Each refresh token works once.</summary>
    [HttpPost("refresh")]
    [ProducesResponseType<SessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SessionResponse>> Refresh(CancellationToken ct)
    {
        if (!Request.Headers.ContainsKey(CsrfHeader)) return InvalidCredentials("Missing the X-Requested-With header.");
        var token = Request.Cookies[RefreshCookie];
        if (string.IsNullOrWhiteSpace(token)) return InvalidCredentials("Session has expired.");
        var result = await auth.RefreshAsync(token, ct);
        if (result is null)
        {
            ClearCookie();
            return InvalidCredentials("Session has expired. Please sign in again.");
        }
        return Session(result);
    }

    /// <summary>
    /// The demo accounts and their password, for the sign-in page of a public demo site
    /// (Seed:ShowDemoLogins with Seed:DemoData). 404 everywhere else, so a real site never offers them.
    /// </summary>
    [HttpGet("demo")]
    [ProducesResponseType<DemoLoginsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<DemoLoginsResponse> Demo([FromServices] IOptions<SeedOptions> seed)
    {
        var demo = DemoLoginsResponse.For(seed.Value);
        if (demo is null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return demo;
    }

    /// <summary>Ends the session: revokes the refresh token and deletes the cookie.</summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var token = Request.Cookies[RefreshCookie];
        if (Request.Headers.ContainsKey(CsrfHeader) && !string.IsNullOrWhiteSpace(token)) await auth.LogoutAsync(token, ct);
        ClearCookie();
        return NoContent();
    }

    private SessionResponse Session(TokenResponse tokens)
    {
        Response.Cookies.Append(RefreshCookie, tokens.RefreshToken, CookieOptions(tokens.RefreshTokenExpiresAt));
        Response.Headers.CacheControl = "no-store";
        return new SessionResponse(tokens.AccessToken, tokens.AccessTokenExpiresAt, tokens.RefreshTokenExpiresAt, tokens.User);
    }

    private void ClearCookie() => Response.Cookies.Delete(RefreshCookie, CookieOptions(null));

    /// <summary>Secure whenever the request is HTTPS, and always when Auth:SecureCookies is on (production).</summary>
    private CookieOptions CookieOptions(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = Request.IsHttps || configuration.GetValue<bool>("Auth:SecureCookies"),
        Path = CookiePath,
        Expires = expires,
        IsEssential = true,
    };

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
