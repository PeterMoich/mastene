using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Accounts;
using TenantManager.Application.Auditing;
using TenantManager.Application.Common;
using TenantManager.Application.Privacy;
using TenantManager.Application.Tenants;
using TenantManager.Domain.Common;
using TenantManager.Infrastructure;
using TenantManager.Infrastructure.Persistence;
using TenantManager.Infrastructure.Storage;

namespace TenantManager.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class PrivacyApiTests(ApiFactory api)
{
    [Fact]
    public async Task A_tenant_downloads_everything_held_about_them_as_a_zip()
    {
        var s = await LeaseScenario.CreateAsync(api, await api.ClientForAsync(Seeded.Landlord));
        await s.ActiveAsync(LeaseScenario.Today.AddMonths(-1));
        var tenant = await s.TenantClientAsync();

        var response = await tenant.GetAsync($"/api/v1/tenants/{s.TenantId}/export");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/zip");
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
        var names = zip.Entries.Select(e => e.FullName).ToList();
        names.ShouldContain("tenant.json");
        names.ShouldContain(n => n.StartsWith("files/leases/", StringComparison.Ordinal) && n.EndsWith(".pdf", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(await new StreamReader(zip.GetEntry("tenant.json")!.Open()).ReadToEndAsync());
        json.RootElement.GetProperty("tenant").GetProperty("email").GetString().ShouldBe(s.TenantEmail);
        json.RootElement.GetProperty("leases").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Only_the_tenant_themself_or_an_admin_can_export()
    {
        var s = await LeaseScenario.CreateAsync(api, await api.ClientForAsync(Seeded.Landlord));
        var other = await api.ClientForAsync(Seeded.OtherTenantWithLogin);

        (await other.GetAsync($"/api/v1/tenants/{s.TenantId}/export")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await s.Landlord.GetAsync($"/api/v1/tenants/{s.TenantId}/export")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await (await api.ClientForAsync(Seeded.Admin)).GetAsync($"/api/v1/tenants/{s.TenantId}/export"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Erasure_is_refused_while_a_lease_is_in_force()
    {
        var s = await LeaseScenario.CreateAsync(api, await api.ClientForAsync(Seeded.Landlord));
        await s.ActiveAsync(LeaseScenario.Today.AddMonths(-1));
        var admin = await api.ClientForAsync(Seeded.Admin);

        await (await admin.PostAsync($"/api/v1/tenants/{s.TenantId}/anonymise", null))
            .ShouldBeAsync(HttpStatusCode.Conflict, "tenant.has_active_lease");
        (await s.Landlord.PostAsync($"/api/v1/tenants/{s.TenantId}/anonymise", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Erasure_replaces_personal_details_and_the_audit_log_does_not_keep_the_old_values()
    {
        var s = await LeaseScenario.CreateAsync(api, await api.ClientForAsync(Seeded.Landlord)); // a tenant with a login, no lease
        var tenant = await s.TenantClientAsync();
        await (await tenant.PostJsonAsync($"/api/v1/tenants/{s.TenantId}/consents",
            new Application.Messaging.ConsentRequest(true, null))).ShouldBeAsync(HttpStatusCode.OK);
        var admin = await api.ClientForAsync(Seeded.Admin);

        var listed = await (await admin.GetAsync($"/api/v1/privacy/tenants?search={Uri.EscapeDataString(s.TenantEmail)}"))
            .ReadAsync<List<PrivacyTenantDto>>();
        listed.ShouldHaveSingleItem().AllowedActions.ShouldContain("anonymise");

        var result = await (await admin.PostAsync($"/api/v1/tenants/{s.TenantId}/anonymise", null)).ReadAsync<AnonymiseResult>();
        result.Kept.ShouldNotBeEmpty();

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == s.TenantId);
        row.FullName.ShouldBe("Former tenant");
        row.Email.ShouldNotContain("lease-");
        row.IsDeleted.ShouldBeTrue();
        (await db.Consents.SingleAsync(c => c.TenantId == s.TenantId)).Phone.ShouldBe("redacted");
        (await db.Users.AnyAsync(u => u.Email == s.TenantEmail)).ShouldBeFalse();

        var audit = await db.AuditLogs.Where(a => a.EntityType == "Tenant" && a.EntityId == s.TenantId.ToString())
            .OrderByDescending(a => a.Id).FirstAsync();
        audit.BeforeJson.ShouldNotBeNull().ShouldContain(AuditRedaction.Erased);
        audit.BeforeJson.ShouldNotContain(s.TenantEmail);

        // The old login no longer works, and erasing twice is refused.
        var login = await api.CreateClient().PostJsonAsync("/api/v1/auth/login", new LoginRequest(s.TenantEmail, s.TenantPassword));
        login.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await (await admin.PostAsync($"/api/v1/tenants/{s.TenantId}/anonymise", null))
            .ShouldBeAsync(HttpStatusCode.Conflict, "tenant.already_anonymised");
    }
}

[Collection(ApiCollection.Name)]
public class SecurityAuditTests(ApiFactory api)
{
    private async Task<List<AuditLogDto>> UserAuditAsync(Guid userId) =>
        (await (await (await api.ClientForAsync(Seeded.Admin)).GetAsync($"/api/v1/audit?entityType=User&entityId={userId}&pageSize=50"))
            .ReadAsync<PagedResult<AuditLogDto>>()).Items.ToList();

    [Fact]
    public async Task Sign_ins_failures_and_user_changes_are_audited_without_secrets()
    {
        var admin = await api.ClientForAsync(Seeded.Admin);
        var email = $"staff-{Guid.NewGuid():N}@mastene.test";
        var created = await (await admin.PostJsonAsync("/api/v1/users",
            new CreateUserRequest(email, "New Staff", "Landlord", "Staff-Password-123"))).ReadAsync<UserDto>();

        (await api.CreateClient().PostJsonAsync("/api/v1/auth/login", new LoginRequest(email, "wrong-password-123")))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await api.LoginAsync(email, "Staff-Password-123");
        await (await admin.PutJsonAsync($"/api/v1/users/{created.Id}/lock", new { locked = true })).ShouldBeAsync(HttpStatusCode.OK);

        var actions = (await UserAuditAsync(created.Id)).Select(a => a.Action).ToList();
        actions.ShouldContain("UserCreated");
        actions.ShouldContain("SignInFailed");
        actions.ShouldContain("SignedIn");
        actions.ShouldContain("Locked");
        (await UserAuditAsync(created.Id)).ShouldAllBe(a =>
            (a.AfterJson == null || !a.AfterJson.Contains("Password")) && (a.BeforeJson == null || !a.BeforeJson.Contains("Password")));
    }
}

/// <summary>The ClamAV scanner against a fake clamd on a local port (the real one is optional in compose).</summary>
public class ClamAvScannerTests
{
    private static async Task<(int Port, Task<byte[]> Received)> FakeClamdAsync(string reply)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var all = new MemoryStream();
            var buffer = new byte[8192];
            // Read until the zero-length chunk that ends INSTREAM.
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0) break;
                all.Write(buffer, 0, read);
                var bytes = all.ToArray();
                if (bytes.Length >= 14 && bytes.AsSpan(bytes.Length - 4).SequenceEqual(new byte[4])) break;
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply + "\0"));
            listener.Stop();
            return all.ToArray();
        });
        await Task.Yield();
        return (port, received);
    }

    private static ClamAvFileScanner Scanner(int port) =>
        new(Options.Create(new ScanningOptions { Provider = "ClamAv", ClamAvHost = "127.0.0.1", ClamAvPort = port, TimeoutSeconds = 5 }),
            NullLogger<ClamAvFileScanner>.Instance);

    [Fact]
    public async Task A_clean_file_passes_and_is_sent_with_the_INSTREAM_protocol()
    {
        var (port, received) = await FakeClamdAsync("stream: OK");
        (await Scanner(port).IsCleanAsync("hello"u8.ToArray(), CancellationToken.None)).ShouldBeTrue();

        var bytes = await received;
        Encoding.ASCII.GetString(bytes, 0, 10).ShouldBe("zINSTREAM\0");
        bytes.AsSpan(10, 4).ToArray().ShouldBe(new byte[] { 0, 0, 0, 5 });
        Encoding.ASCII.GetString(bytes, 14, 5).ShouldBe("hello");
    }

    [Fact]
    public async Task An_infected_file_is_refused()
    {
        var (port, _) = await FakeClamdAsync("stream: Eicar-Signature FOUND");
        (await Scanner(port).IsCleanAsync("X5O!P%@AP"u8.ToArray(), CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task An_unreachable_scanner_fails_closed()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // nothing listens here now

        (await Should.ThrowAsync<DomainRuleException>(() => Scanner(port).IsCleanAsync(new byte[10], CancellationToken.None)))
            .Code.ShouldBe("file.scan_unavailable");
    }
}

public class FeatureFlagTests
{
    [Fact]
    public void Features_are_on_unless_switched_off()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Features:Reminders"] = "false",
        }).Build();
        var flags = new ConfigurationFeatureFlags(config);

        flags.IsEnabled(Features.Reminders).ShouldBeFalse();
        flags.IsEnabled(Features.WhatsAppSending).ShouldBeTrue();
    }
}
