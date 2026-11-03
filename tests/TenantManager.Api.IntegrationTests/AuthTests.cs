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
        var tokens = await api.LoginAsync(Seeded.Landlord);

        tokens.AccessToken.ShouldNotBeNullOrWhiteSpace();
        tokens.RefreshToken.ShouldNotBeNullOrWhiteSpace();
        tokens.User.Role.ShouldBe("Landlord");
        tokens.AccessTokenExpiresAt.ShouldBeLessThan(tokens.RefreshTokenExpiresAt);
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
        using var client = api.CreateClient();
        var first = await api.LoginAsync(Seeded.Landlord);

        var refreshed = await client.PostJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken));
        await refreshed.ShouldBeAsync(HttpStatusCode.OK);
        var second = await refreshed.ReadAsync<TokenResponse>();
        second.RefreshToken.ShouldNotBe(first.RefreshToken);

        // Replaying the old token looks like theft: it fails, and so does the newer token.
        var replay = await client.PostJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken));
        await replay.ShouldBeAsync(HttpStatusCode.Unauthorized);
        var afterReplay = await client.PostJsonAsync("/api/v1/auth/refresh", new RefreshRequest(second.RefreshToken));
        await afterReplay.ShouldBeAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        using var client = api.CreateClient();
        var tokens = await api.LoginAsync(Seeded.Landlord);

        (await client.PostJsonAsync("/api/v1/auth/logout", new RefreshRequest(tokens.RefreshToken)))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await (await client.PostJsonAsync("/api/v1/auth/refresh", new RefreshRequest(tokens.RefreshToken)))
            .ShouldBeAsync(HttpStatusCode.Unauthorized);
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
