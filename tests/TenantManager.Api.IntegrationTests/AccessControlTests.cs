using System.Net;
using Shouldly;
using TenantManager.Application.Accounts;
using TenantManager.Application.Common;
using TenantManager.Application.Tenants;

namespace TenantManager.Api.IntegrationTests;

/// <summary>BR01: a tenant only ever sees their own data; roles only reach their own endpoints.</summary>
[Collection(ApiCollection.Name)]
public class AccessControlTests(ApiFactory api)
{
    private async Task<Guid> TenantIdOf(string email)
    {
        var landlord = await api.ClientForAsync(Seeded.Landlord);
        var page = await (await landlord.GetAsync($"/api/v1/tenants?search={Uri.EscapeDataString(email)}"))
            .ReadAsync<PagedResult<TenantDto>>();
        return page.Items.Single(t => t.Email == email).Id;
    }

    [Fact]
    public async Task BR01_Tenant_can_read_their_own_record()
    {
        var tenant = await api.ClientForAsync(Seeded.TenantWithLogin);
        var ownId = await TenantIdOf(Seeded.TenantWithLogin);

        var response = await tenant.GetAsync($"/api/v1/tenants/{ownId}");

        await response.ShouldBeAsync(HttpStatusCode.OK);
        (await response.ReadAsync<TenantDto>()).Email.ShouldBe(Seeded.TenantWithLogin);
    }

    [Fact]
    public async Task BR01_Tenant_gets_404_for_another_tenants_record()
    {
        var tenant = await api.ClientForAsync(Seeded.TenantWithLogin);
        var otherId = await TenantIdOf(Seeded.OtherTenantWithLogin);

        await (await tenant.GetAsync($"/api/v1/tenants/{otherId}")).ShouldBeAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Theory]
    [InlineData("/api/v1/tenants")]
    [InlineData("/api/v1/properties")]
    [InlineData("/api/v1/users")]
    [InlineData("/api/v1/audit")]
    public async Task BR01_Tenant_cannot_reach_landlord_or_admin_lists(string url)
    {
        var tenant = await api.ClientForAsync(Seeded.TenantWithLogin);
        (await tenant.GetAsync(url)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task BR01_Tenant_cannot_change_their_own_record()
    {
        var tenant = await api.ClientForAsync(Seeded.TenantWithLogin);
        var ownId = await TenantIdOf(Seeded.TenantWithLogin);

        var response = await tenant.PutJsonAsync($"/api/v1/tenants/{ownId}",
            new TenantRequest("Changed", "Name", Seeded.TenantWithLogin, "0825550101"));
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("/api/v1/properties")]
    [InlineData("/api/v1/tenants")]
    public async Task Admin_manages_users_only_not_property_or_tenant_data(string url)
    {
        var admin = await api.ClientForAsync(Seeded.Admin);
        (await admin.GetAsync(url)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Landlord_cannot_manage_users()
    {
        var landlord = await api.ClientForAsync(Seeded.Landlord);
        (await landlord.GetAsync("/api/v1/users")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admin_cannot_lock_their_own_account()
    {
        var admin = await api.ClientForAsync(Seeded.Admin);
        var me = await (await admin.GetAsync("/api/v1/me")).ReadAsync<MeDto>();

        await (await admin.PutJsonAsync($"/api/v1/users/{me.Id}/lock", new SetLockRequest(true)))
            .ShouldBeAsync(HttpStatusCode.Conflict, "user.self_change");
    }
}
