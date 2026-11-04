using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;
using Hangfire;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Formatting.Compact;
using TenantManager.Api.Infrastructure;
using TenantManager.Application;
using TenantManager.Application.Abstractions;
using TenantManager.Infrastructure;
using TenantManager.Infrastructure.Identity;
using TenantManager.Infrastructure.Jobs;
using TenantManager.Infrastructure.Persistence;

// One culture for parsing and formatting everywhere (amounts like 6500.50 in forms, messages, PDFs),
// whatever the machine's regional settings are.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((services, logger) => logger
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
    ctx.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? ctx.HttpContext.TraceIdentifier);
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

builder.Services.AddApiSecurity(builder.Configuration);
builder.Services.AddApiDocumentation();
builder.Services.AddObservability(builder.Configuration);
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

if (builder.Configuration.GetValue<bool>("ReverseProxy:Enabled"))
{
    // Behind nginx (compose) or Azure App Service: trust X-Forwarded-For/-Proto only from the proxy's
    // network, and only one hop, so a client can't fake its IP to dodge rate limits (ADR-0019).
    var networks = builder.Configuration.GetSection("ReverseProxy:KnownNetworks").Get<string[]>()
                   ?? ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8", "::1/128"];
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.ForwardLimit = 1;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
        foreach (var cidr in networks) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
    });
}
builder.Services.AddHsts(o => o.MaxAge = TimeSpan.FromDays(365));

var app = builder.Build();

// `dotnet TenantManager.Api.dll migrate` applies migrations, seeds, and exits (the compose "migrator" service).
var migrateOnly = args.Contains("migrate", StringComparer.OrdinalIgnoreCase);
if (migrateOnly || app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var seeder = scope.ServiceProvider.GetRequiredService<DataSeeder>();
    await seeder.MigrateAsync(CancellationToken.None);
    await seeder.SeedAsync(scope.ServiceProvider.GetRequiredService<IOptions<SeedOptions>>().Value,
        CancellationToken.None);
    if (migrateOnly) return;
}

if (app.Configuration.GetValue<bool>("Jobs:Enabled"))
{
    app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<LeaseLifecycleJob>(
        LeaseLifecycleJob.Id,
        job => job.RunAsync(CancellationToken.None),
        app.Configuration["Jobs:LeaseLifecycleCron"] ?? LeaseLifecycleJob.DefaultCron,
        new RecurringJobOptions { TimeZone = BusinessTime.Zone });
    app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<IssueSlaJob>(
        IssueSlaJob.Id,
        job => job.RunAsync(CancellationToken.None),
        app.Configuration["Jobs:IssueSlaCron"] ?? IssueSlaJob.DefaultCron,
        new RecurringJobOptions { TimeZone = BusinessTime.Zone });
    app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<MessageDispatchJob>(
        MessageDispatchJob.Id,
        job => job.RunAsync(CancellationToken.None),
        app.Configuration["Jobs:MessageDispatchCron"] ?? MessageDispatchJob.DefaultCron,
        new RecurringJobOptions { TimeZone = BusinessTime.Zone });
    app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<ReminderJob>(
        ReminderJob.Id,
        job => job.RunAsync(CancellationToken.None),
        app.Configuration["Jobs:ReminderCron"] ?? ReminderJob.DefaultCron,
        new RecurringJobOptions { TimeZone = BusinessTime.Zone });
}

if (app.Services.GetRequiredService<JwtSigningKey>().IsGenerated)
    app.Logger.LogWarning("Jwt:SigningKey is not set; using a random development key. Tokens stop working on restart.");

if (app.Configuration.GetValue<bool>("ReverseProxy:Enabled")) app.UseForwardedHeaders();

app.UseSerilogRequestLogging();
if (!app.Environment.IsDevelopment()) app.UseHsts(); // only sent on HTTPS responses
app.UseExceptionHandler();
app.UseStatusCodePages();

app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v1/swagger.json", "Tenant Manager API v1"));
}

if (app.Configuration.GetValue<bool>("Jobs:Enabled")) app.UseJobsDashboard(); // Admin only, see JobsDashboard

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") })
    .AllowAnonymous();

await app.RunAsync();

/// <summary>Exposed so integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program;
