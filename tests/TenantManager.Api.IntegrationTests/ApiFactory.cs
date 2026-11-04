using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Shouldly;
using Testcontainers.MsSql;
using TenantManager.Application.Accounts;

namespace TenantManager.Api.IntegrationTests;

/// <summary>
/// Hosts the real API against a real SQL Server in Docker (Testcontainers). One database for the
/// whole run; each test creates its own data with unique names so tests don't interfere.
/// Migrations and the synthetic seed data are applied on startup, exactly as in docker-compose.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string SeedPassword = "Integration-Test-Pass-1";
    public const string WhatsAppSecret = "integration-tests-whatsapp-app-secret";
    public const string WhatsAppVerifyToken = "integration-tests-verify-token";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    /// <summary>Lease PDFs written by the tests (Storage:Provider=Local). Deleted after the run.</summary>
    public string FilesPath { get; } = Path.Combine(Path.GetTempPath(), $"tm-tests-{Guid.NewGuid():N}");

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _sql.StartAsync();
        ConnectionString = new SqlConnectionStringBuilder(_sql.GetConnectionString())
        {
            InitialCatalog = "TenantManagerTests",
            TrustServerCertificate = true,
        }.ConnectionString;

        // Environment variables are read by Program.cs's own configuration, which is the most
        // reliable way to configure a minimal-hosting app under WebApplicationFactory.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", ConnectionString);
        Environment.SetEnvironmentVariable("Jwt__SigningKey", "integration-tests-signing-key-0123456789abcdef");
        Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "true");
        Environment.SetEnvironmentVariable("Seed__DemoData", "true");
        Environment.SetEnvironmentVariable("Seed__Password", SeedPassword);
        Environment.SetEnvironmentVariable("RateLimits__AuthPerMinute", "100000");
        Environment.SetEnvironmentVariable("RateLimits__GeneralPerMinute", "100000");
        Environment.SetEnvironmentVariable("Storage__Provider", "Local");
        Environment.SetEnvironmentVariable("Storage__LocalPath", FilesPath);
        Environment.SetEnvironmentVariable("Jobs__Enabled", "false"); // tests call the lease checks endpoint instead
        Environment.SetEnvironmentVariable("WhatsApp__Provider", "Fake");
        Environment.SetEnvironmentVariable("WhatsApp__AppSecret", WhatsAppSecret);
        Environment.SetEnvironmentVariable("WhatsApp__VerifyToken", WhatsAppVerifyToken);

        _ = Server; // start the host now so migrations run once, before any test
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _sql.DisposeAsync();
        if (Directory.Exists(FilesPath)) Directory.Delete(FilesPath, recursive: true);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");

    public async Task<HttpClient> ClientForAsync(string email, string password = SeedPassword)
    {
        var tokens = await LoginAsync(email, password);
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    public async Task<SessionResponse> LoginAsync(string email, string password = SeedPassword) =>
        (await LoginWithCookieAsync(email, password)).Session;

    /// <summary>Signs in and returns the refresh cookie too ("tm_refresh=..."), as a browser would keep it.</summary>
    public async Task<(SessionResponse Session, string Cookie)> LoginWithCookieAsync(string email, string password = SeedPassword)
    {
        using var anonymous = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var response = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password), Json);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return ((await response.Content.ReadFromJsonAsync<SessionResponse>(Json))!, RefreshCookieOf(response));
    }

    public static string RefreshCookieOf(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])
            .Last(c => c.StartsWith("tm_refresh=", StringComparison.Ordinal));

    /// <summary>Posts to an auth endpoint with the refresh cookie and the anti-forgery header, as the web app does.</summary>
    public async Task<HttpResponseMessage> PostWithCookieAsync(string url, string? cookie, bool csrfHeader = true)
    {
        using var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        if (csrfHeader) request.Headers.Add("X-Requested-With", "TenantManager");
        return await client.SendAsync(request);
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}

public static class Seeded
{
    public const string Admin = "admin@mastene.test";
    public const string Landlord = "landlord@mastene.test";
    public const string TenantWithLogin = "sipho.dlamini@example.com";
    public const string OtherTenantWithLogin = "lerato.mokoena@example.com";
}

public static class HttpExtensions
{
    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(ApiFactory.Json))!;

    public static Task<HttpResponseMessage> PostJsonAsync<T>(this HttpClient client, string url, T body) =>
        client.PostAsJsonAsync(url, body, ApiFactory.Json);

    public static Task<HttpResponseMessage> PutJsonAsync<T>(this HttpClient client, string url, T body) =>
        client.PutAsJsonAsync(url, body, ApiFactory.Json);

    /// <summary>Asserts the status and, for errors, the ProblemDetails "code".</summary>
    public static async Task ShouldBeAsync(this HttpResponseMessage response, System.Net.HttpStatusCode status,
        string? code = null)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(status, body);
        if (code is null) return;
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("code").GetString().ShouldBe(code);
    }
}
