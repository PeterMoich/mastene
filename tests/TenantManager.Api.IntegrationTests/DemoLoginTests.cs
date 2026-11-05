using System.Net;
using Shouldly;
using TenantManager.Infrastructure.Persistence;

namespace TenantManager.Api.IntegrationTests;

public class DemoLoginRuleTests
{
    [Fact]
    public void Demo_logins_are_offered_only_with_demo_data_the_switch_and_a_password()
    {
        DemoLoginsResponse.For(new SeedOptions { DemoData = true, ShowDemoLogins = true, Password = "Demo-1" })
            .ShouldNotBeNull().Password.ShouldBe("Demo-1");

        DemoLoginsResponse.For(new SeedOptions { DemoData = true, ShowDemoLogins = false, Password = "Demo-1" }).ShouldBeNull();
        DemoLoginsResponse.For(new SeedOptions { DemoData = false, ShowDemoLogins = true, Password = "Demo-1" }).ShouldBeNull();
        DemoLoginsResponse.For(new SeedOptions { DemoData = true, ShowDemoLogins = true, Password = " " }).ShouldBeNull();
    }

    [Fact]
    public void The_admin_is_never_offered()
    {
        DemoLoginsResponse.SeededAccounts.ShouldNotContain(a => a.Role == "Admin" || a.Email == DataSeeder.AdminEmail);
        DemoLoginsResponse.SeededAccounts.ShouldContain(a => a.Email == DataSeeder.LandlordEmail);
    }
}

[Collection(ApiCollection.Name)]
public class DemoLoginEndpointTests(ApiFactory api)
{
    [Fact]
    public async Task Without_the_switch_the_sign_in_page_gets_no_demo_logins()
    {
        using var client = api.CreateClient();
        (await client.GetAsync("/api/v1/auth/demo")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
