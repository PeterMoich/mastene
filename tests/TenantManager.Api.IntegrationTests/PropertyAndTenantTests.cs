using System.Net;
using Shouldly;
using TenantManager.Application.Accounts;
using TenantManager.Application.Common;
using TenantManager.Application.Properties;
using TenantManager.Application.Tenants;
using TenantManager.Domain.Tenants;

namespace TenantManager.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class PropertyApiTests(ApiFactory api)
{
    private static PropertyRequest NewRequest(string name) =>
        new(name, "1 Test Street", null, "Johannesburg", "Gauteng", "2001");

    [Fact]
    public async Task Create_property_add_units_and_read_back()
    {
        var landlord = await api.ClientForAsync(Seeded.Landlord);
        var name = $"Test Court {Guid.NewGuid():N}";

        var created = await landlord.PostJsonAsync("/api/v1/properties", NewRequest(name));
        await created.ShouldBeAsync(HttpStatusCode.Created);
        var property = await created.ReadAsync<PropertyDto>();
        created.Headers.Location!.ToString().ShouldBe($"/api/v1/properties/{property.Id}");

        await (await landlord.PostJsonAsync($"/api/v1/properties/{property.Id}/units", new UnitRequest("Flat 1", 6500m, 6500m)))
            .ShouldBeAsync(HttpStatusCode.Created);
        await (await landlord.PostJsonAsync($"/api/v1/properties/{property.Id}/units", new UnitRequest("Flat 2", 7200.50m, 0m)))
            .ShouldBeAsync(HttpStatusCode.Created);

        var detail = await (await landlord.GetAsync($"/api/v1/properties/{property.Id}")).ReadAsync<PropertyDto>();
        detail.Units.Select(u => u.Label).ShouldBe(["Flat 1", "Flat 2"]);
        detail.Units.ShouldAllBe(u => u.Status == "Vacant");
        detail.Units[1].RentAmount.ShouldBe(7200.50m);

        var list = await (await landlord.GetAsync($"/api/v1/properties?search={name}"))
            .ReadAsync<PagedResult<PropertySummaryDto>>();
        list.TotalCount.ShouldBe(1);
        list.Items[0].UnitCount.ShouldBe(2);
    }

    [Fact]
    public async Task Invalid_property_returns_400_with_field_errors()
    {
        var landlord = await api.ClientForAsync(Seeded.Landlord);
        var response = await landlord.PostJsonAsync("/api/v1/properties",
            new PropertyRequest("", "1 Street", null, "City", "Atlantis", "12"));

        await response.ShouldBeAsync(HttpStatusCode.BadRequest, "validation");
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("\"name\"");
        body.ShouldContain("\"province\"");
        body.ShouldContain("\"postalCode\"");
        body.ShouldContain("traceId");
    }

    [Fact]
    public async Task Duplicate_unit_label_returns_409()
    {
        var landlord = await api.ClientForAsync(Seeded.Landlord);
        var property = await (await landlord.PostJsonAsync("/api/v1/properties", NewRequest($"Dup {Guid.NewGuid():N}")))
            .ReadAsync<PropertyDto>();
        await landlord.PostJsonAsync($"/api/v1/properties/{property.Id}/units", new UnitRequest("Flat 1", 5000m, 0m));

        await (await landlord.PostJsonAsync($"/api/v1/properties/{property.Id}/units", new UnitRequest("FLAT 1", 5000m, 0m)))
            .ShouldBeAsync(HttpStatusCode.Conflict, "unit.label_taken");
    }

    [Fact]
    public async Task Paging_and_sorting_work_on_units()
    {
        var landlord = await api.ClientForAsync(Seeded.Landlord);
        var property = await (await landlord.PostJsonAsync("/api/v1/properties", NewRequest($"Paging {Guid.NewGuid():N}")))
            .ReadAsync<PropertyDto>();
        foreach (var (label, rent) in new[] { ("A", 3000m), ("B", 9000m), ("C", 6000m) })
            await landlord.PostJsonAsync($"/api/v1/properties/{property.Id}/units", new UnitRequest(label, rent, 0m));

        var page = await (await landlord.GetAsync($"/api/v1/properties/{property.Id}/units?sort=-rentAmount&page=1&pageSize=2"))
            .ReadAsync<PagedResult<UnitDto>>();

        page.TotalCount.ShouldBe(3);
        page.Items.Select(u => u.Label).ShouldBe(["B", "C"]);
    }

    [Fact]
    public async Task Deleted_property_disappears_but_stays_in_the_database()
    {
        var landlord = await api.ClientForAsync(Seeded.Landlord);
        var property = await (await landlord.PostJsonAsync("/api/v1/properties", NewRequest($"Delete {Guid.NewGuid():N}")))
            .ReadAsync<PropertyDto>();

        (await landlord.DeleteAsync($"/api/v1/properties/{property.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await landlord.GetAsync($"/api/v1/properties/{property.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(api.ConnectionString);
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT IsDeleted FROM Properties WHERE Id = @id", connection);
        command.Parameters.AddWithValue("@id", property.Id);
        ((bool)(await command.ExecuteScalarAsync())!).ShouldBeTrue();
    }
}

[Collection(ApiCollection.Name)]
public class TenantApiTests(ApiFactory api)
{
    private static TenantRequest NewTenant(string email) => new("Test", "Tenant", email, "072 555 0199");

    private static string UniqueEmail() => $"tenant-{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task BR19_Tenant_without_lease_or_balance_is_soft_deleted()
    {
        var landlord = await api.ClientForAsync(Seeded.Landlord);
        var tenant = await (await landlord.PostJsonAsync("/api/v1/tenants", NewTenant(UniqueEmail()))).ReadAsync<TenantDto>();

        (await landlord.DeleteAsync($"/api/v1/tenants/{tenant.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await landlord.GetAsync($"/api/v1/tenants/{tenant.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task BR19_Tenant_with_active_lease_cannot_be_deleted()
    {
        var landlord = await api.ClientForAsync(Seeded.Landlord);
        var tenant = await (await landlord.PostJsonAsync("/api/v1/tenants", NewTenant(UniqueEmail()))).ReadAsync<TenantDto>();
        api.Obligations.Set(tenant.Id, new TenantObligations(HasActiveLease: true, OutstandingBalance: 0));

        await (await landlord.DeleteAsync($"/api/v1/tenants/{tenant.Id}"))
            .ShouldBeAsync(HttpStatusCode.Conflict, "tenant.has_active_lease");
        (await landlord.GetAsync($"/api/v1/tenants/{tenant.Id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task BR19_Tenant_with_outstanding_balance_cannot_be_deleted()
    {
        var landlord = await api.ClientForAsync(Seeded.Landlord);
        var tenant = await (await landlord.PostJsonAsync("/api/v1/tenants", NewTenant(UniqueEmail()))).ReadAsync<TenantDto>();
        api.Obligations.Set(tenant.Id, new TenantObligations(HasActiveLease: false, OutstandingBalance: 1500m));

        await (await landlord.DeleteAsync($"/api/v1/tenants/{tenant.Id}"))
            .ShouldBeAsync(HttpStatusCode.Conflict, "tenant.has_outstanding_balance");
    }

    [Fact]
    public async Task Duplicate_tenant_email_returns_409()
    {
        var landlord = await api.ClientForAsync(Seeded.Landlord);
        var email = UniqueEmail();
        await landlord.PostJsonAsync("/api/v1/tenants", NewTenant(email));

        await (await landlord.PostJsonAsync("/api/v1/tenants", NewTenant(email.ToUpperInvariant())))
            .ShouldBeAsync(HttpStatusCode.Conflict, "tenant.email_taken");
    }

    [Fact]
    public async Task Landlord_can_give_a_tenant_a_login_that_only_sees_their_own_record()
    {
        var landlord = await api.ClientForAsync(Seeded.Landlord);
        var email = UniqueEmail();
        var tenant = await (await landlord.PostJsonAsync("/api/v1/tenants", NewTenant(email))).ReadAsync<TenantDto>();
        tenant.HasLogin.ShouldBeFalse();

        var created = await landlord.PostAsync($"/api/v1/tenants/{tenant.Id}/login", null);
        await created.ShouldBeAsync(HttpStatusCode.Created);
        var login = await created.ReadAsync<TenantLoginCreated>();
        login.TemporaryPassword.Length.ShouldBe(16);

        await (await landlord.PostAsync($"/api/v1/tenants/{tenant.Id}/login", null))
            .ShouldBeAsync(HttpStatusCode.Conflict, "tenant.login_exists");

        var asTenant = await api.ClientForAsync(email, login.TemporaryPassword);
        (await (await asTenant.GetAsync($"/api/v1/tenants/{tenant.Id}")).ReadAsync<TenantDto>()).HasLogin.ShouldBeTrue();
    }

    [Fact]
    public async Task Deleting_a_tenant_locks_their_login()
    {
        var landlord = await api.ClientForAsync(Seeded.Landlord);
        var email = UniqueEmail();
        var tenant = await (await landlord.PostJsonAsync("/api/v1/tenants", NewTenant(email))).ReadAsync<TenantDto>();
        var login = await (await landlord.PostAsync($"/api/v1/tenants/{tenant.Id}/login", null)).ReadAsync<TenantLoginCreated>();

        await landlord.DeleteAsync($"/api/v1/tenants/{tenant.Id}");

        using var anonymous = api.CreateClient();
        await (await anonymous.PostJsonAsync("/api/v1/auth/login", new LoginRequest(email, login.TemporaryPassword)))
            .ShouldBeAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Search_finds_tenants_by_name_email_or_phone()
    {
        var landlord = await api.ClientForAsync(Seeded.Landlord);
        var page = await (await landlord.GetAsync("/api/v1/tenants?search=mokoena")).ReadAsync<PagedResult<TenantDto>>();

        page.Items.ShouldContain(t => t.Email == Seeded.OtherTenantWithLogin);
        page.Items.Single(t => t.Email == Seeded.OtherTenantWithLogin).HasLogin.ShouldBeTrue();
    }
}
