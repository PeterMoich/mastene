using Hangfire;
using Microsoft.Extensions.Logging;
using TenantManager.Application.Leases;

namespace TenantManager.Infrastructure.Jobs;

/// <summary>Hangfire wrapper for the daily lease run (ADR-0009). The rules live in the domain.</summary>
public sealed class LeaseLifecycleJob(LeaseLifecycleService lifecycle, ILogger<LeaseLifecycleJob> logger)
{
    public const string Id = "lease-lifecycle";

    /// <summary>Every day at 00:15 South African time.</summary>
    public const string DefaultCron = "15 0 * * *";

    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var summary = await lifecycle.RunAsync(cancellationToken);
        logger.LogInformation("Lease lifecycle run finished: {@Summary}", summary);
    }
}
