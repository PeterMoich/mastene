using System.Net;
using Shouldly;
using TenantManager.Application.Accounts;

namespace TenantManager.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AuthTests(ApiFactory api)
{
    [Fact]
    public async Task Login_returns_tokens_and_the_users_role()
    {
        using var client = api.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        var response = await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest(Seeded.Landlord, ApiFactory.SeedPassword));
        var tokens = await response.ReadAsync<SessionResponse>();

        tokens.AccessToken.ShouldNotBeNullOrWhiteSpace();
        tokens.User.Role.ShouldBe("Landlord");
        tokens.AccessTokenExpiresAt.ShouldBeLessThan(tokens.RefreshTokenExpiresAt);
        // The refresh token is never in the body, only in an HttpOnly, SameSite=Strict cookie for the auth endpoints.
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("refreshToken\"");
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("tm_refresh=", StringComparison.Ordinal));
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("samesite=strict", Case.Insensitive);
        cookie.ShouldContain("path=/api/v1/auth", Case.Insensitive);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_get_the_same_401()
    {
        using var client = api.CreateClient();
        var wrongPassword = await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest(Seeded.Landlord, "nope-nope-nope"));
        var unknownEmail = await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest("nobody@example.com", "nope-nope-nope"));

        await wrongPassword.ShouldBeAsync(HttpStatusCode.Unauthorized, "auth.invalid");
        await unknownEmail.ShouldBeAsync(HttpStatusCode.Unauthorized, "auth.invalid");
    }

    [Fact]
    public async Task Requests_without_a_token_get_401()
    {
        using var client = api.CreateClient();
        (await client.GetAsync("/api/v1/properties")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Health_endpoints_are_public()
    {
        using var client = api.CreateClient();
        (await client.GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_reuse_revokes_the_whole_session()
    {
        var (_, first) = await api.LoginWithCookieAsync(Seeded.Landlord);

        var refreshed = await api.PostWithCookieAsync("/api/v1/auth/refresh", first);
        await refreshed.ShouldBeAsync(HttpStatusCode.OK);
        (await refreshed.ReadAsync<SessionResponse>()).AccessToken.ShouldNotBeNullOrWhiteSpace();
        var second = ApiFactory.RefreshCookieOf(refreshed);
        second.ShouldNotBe(first);

        // Replaying the old token looks like theft: it fails, and so does the newer token.
        await (await api.PostWithCookieAsync("/api/v1/auth/refresh", first)).ShouldBeAsync(HttpStatusCode.Unauthorized);
        await (await api.PostWithCookieAsync("/api/v1/auth/refresh", second)).ShouldBeAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_needs_the_cookie_and_the_anti_forgery_header()
    {
        var (_, cookie) = await api.LoginWithCookieAsync(Seeded.Landlord);

        await (await api.PostWithCookieAsync("/api/v1/auth/refresh", null)).ShouldBeAsync(HttpStatusCode.Unauthorized);
        // A cross-site form post would carry no custom header.
        await (await api.PostWithCookieAsync("/api/v1/auth/refresh", cookie, csrfHeader: false))
            .ShouldBeAsync(HttpStatusCode.Unauthorized);
        // The cookie wasn't used up by the refused call.
        await (await api.PostWithCookieAsync("/api/v1/auth/refresh", cookie)).ShouldBeAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        var (_, cookie) = await api.LoginWithCookieAsync(Seeded.Landlord);

        (await api.PostWithCookieAsync("/api/v1/auth/logout", cookie)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await (await api.PostWithCookieAsync("/api/v1/auth/refresh", cookie)).ShouldBeAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account()
    {
        var admin = await api.ClientForAsync(Seeded.Admin);
        var email = $"lockout-{Guid.NewGuid():N}@example.com";
        const string password = "Correct-Password-123";
        await (await admin.PostJsonAsync("/api/v1/users", new CreateUserRequest(email, "Lockout Test", "Landlord", password)))
            .ShouldBeAsync(HttpStatusCode.Created);

        using var client = api.CreateClient();
        for (var i = 0; i < 5; i++)
            await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest(email, "wrong-password-xyz"));

        await (await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest(email, password)))
            .ShouldBeAsync(HttpStatusCode.Unauthorized, "auth.invalid");
    }

    [Fact]
    public async Task Me_returns_the_signed_in_user()
    {
        var client = await api.ClientForAsync(Seeded.TenantWithLogin);
        var me = await (await client.GetAsync("/api/v1/me")).ReadAsync<MeDto>();

        me.Email.ShouldBe(Seeded.TenantWithLogin);
        me.Role.ShouldBe("Tenant");
        me.TenantId.ShouldNotBeNull();
    }
}
