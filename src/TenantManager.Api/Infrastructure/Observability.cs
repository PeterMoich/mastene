using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TenantManager.Application.Abstractions;

namespace TenantManager.Api.Infrastructure;

/// <summary>
/// OpenTelemetry traces and metrics (ADR-0018). Where they go depends on configuration:
/// APPLICATIONINSIGHTS_CONNECTION_STRING sends everything to Application Insights (Azure);
/// otherwise OTEL_EXPORTER_OTLP_ENDPOINT sends to any OpenTelemetry collector; with neither, nothing
/// is exported and the overhead is negligible. Logs stay on Serilog's JSON console output, which
/// carries the trace and span ids, so log lines and traces can be joined.
/// </summary>
public static class Observability
{
    public const string ServiceName = "tenant-manager-api";

    public static IServiceCollection AddObservability(this IServiceCollection services, IConfiguration configuration)
    {
        var version = typeof(Observability).Assembly.GetName().Version?.ToString() ?? "dev";
        var otel = services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(ServiceName, serviceVersion: version,
                serviceInstanceId: Environment.MachineName));

        // Health probes run every few seconds; they would drown the real traffic in traces.
        static bool NotHealth(HttpContext c) => !c.Request.Path.StartsWithSegments("/health");

        if (!string.IsNullOrWhiteSpace(configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            // The Azure Monitor distro adds ASP.NET Core, HttpClient and SQL Client instrumentation itself.
            otel.UseAzureMonitor()
                .WithTracing(t => t.AddSource(AppTelemetry.Name))
                .WithMetrics(m => m.AddMeter(AppTelemetry.Name));
            services.Configure<OpenTelemetry.Instrumentation.AspNetCore.AspNetCoreTraceInstrumentationOptions>(o => o.Filter = NotHealth);
            return services;
        }

        otel.WithTracing(t => t
                .AddAspNetCoreInstrumentation(o => o.Filter = NotHealth)
                .AddHttpClientInstrumentation()
                .AddSource(AppTelemetry.Name))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(AppTelemetry.Name));
        if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) otel.UseOtlpExporter();
        return services;
    }
}
