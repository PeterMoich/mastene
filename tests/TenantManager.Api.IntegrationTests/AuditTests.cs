using System.Net;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Shouldly;
using TenantManager.Application.Auditing;
using TenantManager.Application.Common;
using TenantManager.Application.Properties;
using TenantManager.Application.Tenants;

namespace TenantManager.Api.IntegrationTests;

/// <summary>BR20: every state change is written to an append-only audit log.</summary>
[Collection(ApiCollection.Name)]
public class AuditTests(ApiFactory api)
{
    private static async Task<IReadOnlyList<AuditLogDto>> AuditFor(HttpClient client, string type, Guid id) =>
        (await (await client.GetAsync($"/api/v1/audit?entityType={type}&entityId={id}"))
            .ReadAsync<PagedResult<AuditLogDto>>()).Items;

    [Fact]
    public async Task BR20_Create_update_and_delete_each_write_exactly_one_row()
    {
        var landlord = await api.ClientForAsync(Seeded.Landlord);
        var me = await (await landlord.GetAsync("/api/v1/me")).ReadAsync<Application.Accounts.MeDto>();
        var request = new TenantRequest("Audit", "Subject", $"audit-{Guid.NewGuid():N}@example.com", "0825550111");

        var tenant = await (await landlord.PostJsonAsync("/api/v1/tenants", request)).ReadAsync<TenantDto>();
        await landlord.PutJsonAsync($"/api/v1/tenants/{tenant.Id}", request with { LastName = "Renamed" });
        await landlord.DeleteAsync($"/api/v1/tenants/{tenant.Id}");

        var rows = await AuditFor(landlord, "Tenant", tenant.Id);
        rows.Select(r => r.Action).ShouldBe(["Deleted", "Updated", "Created"]); // newest first
        rows.ShouldAllBe(r => r.UserId == me.Id);

        var update = rows.Single(r => r.Action == "Updated");
        using var before = JsonDocument.Parse(update.BeforeJson!);
        using var after = JsonDocument.Parse(update.AfterJson!);
        before.RootElement.GetProperty("LastName").GetString().ShouldBe("Subject");
        after.RootElement.GetProperty("LastName").GetString().ShouldBe("Renamed");
        after.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(["LastName"]); // only what changed
    }

    [Fact]
    public async Task BR20_Unit_changes_are_audited_too()
    {
        var landlord = await api.ClientForAsync(Seeded.Landlord);
        var property = await (await landlord.PostJsonAsync("/api/v1/properties",
            new PropertyRequest($"Audit {Guid.NewGuid():N}", "1 Street", null, "Durban", "KwaZulu-Natal", "4001")))
            .ReadAsync<PropertyDto>();
        var unit = await (await landlord.PostJsonAsync($"/api/v1/properties/{property.Id}/units",
            new UnitRequest("Flat 1", 5000m, 5000m))).ReadAsync<UnitDto>();
        await landlord.PutJsonAsync($"/api/v1/units/{unit.Id}", new UnitRequest("Flat 1", 5250m, 5000m));

        var rows = await AuditFor(landlord, "Unit", unit.Id);
        rows.Select(r => r.Action).ShouldBe(["Updated", "Created"]);
        using var after = JsonDocument.Parse(rows[0].AfterJson!);
        after.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(["RentAmount"]);
        after.RootElement.GetProperty("RentAmount").GetDecimal().ShouldBe(5250m);
    }

    [Fact]
    public async Task BR20_Audit_rows_cannot_be_changed_or_deleted_in_the_database()
    {
        await using var connection = new SqlConnection(api.ConnectionString);
        await connection.OpenAsync();

        foreach (var sql in new[] { "UPDATE AuditLogs SET Action = 'Tampered'", "DELETE FROM AuditLogs" })
        {
            await using var command = new SqlCommand(sql, connection);
            var ex = await Should.ThrowAsync<SqlException>(() => command.ExecuteNonQueryAsync());
            ex.Message.ShouldContain("append-only");
        }
    }

    [Fact]
    public async Task Tenants_cannot_read_the_audit_log()
    {
        var tenant = await api.ClientForAsync(Seeded.TenantWithLogin);
        (await tenant.GetAsync("/api/v1/audit")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
