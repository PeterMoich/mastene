using System.Net;
using Shouldly;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Accounts;
using TenantManager.Application.Leases;
using TenantManager.Application.Properties;
using TenantManager.Application.Tenants;

namespace TenantManager.Api.IntegrationTests;

/// <summary>
/// Builds the data a lease test needs through the public API: a new property with units, and a
/// new tenant with a login. Every scenario is fresh, so tests never collide with each other.
/// </summary>
public sealed class LeaseScenario
{
    private LeaseScenario(ApiFactory api, HttpClient landlord) => (Api, Landlord) = (api, landlord);

    public ApiFactory Api { get; }
    public HttpClient Landlord { get; }
    public Guid PropertyId { get; private set; }
    public Guid UnitId { get; private set; }
    public Guid SecondUnitId { get; private set; }
    public Guid TenantId { get; private set; }
    public string TenantEmail { get; private set; } = string.Empty;
    public string TenantPassword { get; private set; } = string.Empty;

    /// <summary>The business date the API uses (South African time).</summary>
    public static DateOnly Today =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, BusinessTime.Zone).DateTime);

    public static async Task<LeaseScenario> CreateAsync(ApiFactory api, HttpClient landlord)
    {
        var scenario = new LeaseScenario(api, landlord);
        var property = await (await landlord.PostJsonAsync("/api/v1/properties",
                new PropertyRequest($"Lease Test {Guid.NewGuid():N}", "1 Test Road", null, "Cape Town", "Western Cape", "8001")))
            .ReadAsync<PropertyDto>();
        scenario.PropertyId = property.Id;
        scenario.UnitId = (await (await landlord.PostJsonAsync($"/api/v1/properties/{property.Id}/units",
            new UnitRequest("Flat 1", 6500m, 6500m))).ReadAsync<UnitDto>()).Id;
        scenario.SecondUnitId = (await (await landlord.PostJsonAsync($"/api/v1/properties/{property.Id}/units",
            new UnitRequest("Flat 2", 7000m, 7000m))).ReadAsync<UnitDto>()).Id;
        await scenario.NewTenantAsync();
        return scenario;
    }

    /// <summary>Adds another tenant (with a login) and makes them this scenario's tenant.</summary>
    public async Task<Guid> NewTenantAsync()
    {
        TenantEmail = $"lease-{Guid.NewGuid():N}@example.com";
        var tenant = await (await Landlord.PostJsonAsync("/api/v1/tenants",
            new TenantRequest("Lease", "Tester", TenantEmail, "0825550188"))).ReadAsync<TenantDto>();
        var login = await (await Landlord.PostAsync($"/api/v1/tenants/{tenant.Id}/login", null))
            .ReadAsync<TenantLoginCreated>();
        TenantId = tenant.Id;
        TenantPassword = login.TemporaryPassword;
        return tenant.Id;
    }

    public Task<HttpClient> TenantClientAsync() => Api.ClientForAsync(TenantEmail, TenantPassword);

    public async Task<LeaseDto> DraftAsync(DateOnly start, Guid? unitId = null, decimal rent = 6500m,
        decimal escalation = 8m)
    {
        var response = await Landlord.PostJsonAsync("/api/v1/leases",
            new LeaseRequest(unitId ?? UnitId, TenantId, start, rent, 6500m, escalation));
        await response.ShouldBeAsync(HttpStatusCode.Created);
        return await response.ReadAsync<LeaseDto>();
    }

    public async Task<LeaseDto> GenerateAsync(Guid leaseId)
    {
        var response = await Landlord.PostAsync($"/api/v1/leases/{leaseId}/generate", null);
        await response.ShouldBeAsync(HttpStatusCode.OK);
        return await response.ReadAsync<LeaseDto>();
    }

    /// <summary>Landlord and tenant both sign.</summary>
    public async Task<LeaseDto> SignBothAsync(Guid leaseId)
    {
        await (await Landlord.PostAsync($"/api/v1/leases/{leaseId}/sign", null)).ShouldBeAsync(HttpStatusCode.OK);
        var tenant = await TenantClientAsync();
        var response = await tenant.PostAsync($"/api/v1/leases/{leaseId}/sign", null);
        await response.ShouldBeAsync(HttpStatusCode.OK);
        return await response.ReadAsync<LeaseDto>();
    }

    public async Task<LeaseDto> ActiveAsync(DateOnly start, decimal rent = 6500m, decimal escalation = 8m)
    {
        var draft = await DraftAsync(start, rent: rent, escalation: escalation);
        await GenerateAsync(draft.Id);
        return await SignBothAsync(draft.Id);
    }

    public async Task<LeaseDto> GetAsync(Guid leaseId) =>
        await (await Landlord.GetAsync($"/api/v1/leases/{leaseId}")).ReadAsync<LeaseDto>();

    public async Task RunChecksAsync() =>
        await (await Landlord.PostAsync("/api/v1/lease-checks/run", null)).ShouldBeAsync(HttpStatusCode.OK);

    public static async Task<LeaseDto> ActiveLeaseAsync(ApiFactory api, HttpClient landlord, DateOnly start) =>
        await (await CreateAsync(api, landlord)).ActiveAsync(start);
}
