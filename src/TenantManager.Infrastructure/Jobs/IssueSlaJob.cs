using Hangfire;
using Microsoft.Extensions.Logging;
using TenantManager.Application.Issues;

namespace TenantManager.Infrastructure.Jobs;

/// <summary>Hangfire wrapper for the issue SLA checks (BR15, ADR-0009). The rules live in the domain.</summary>
public sealed class IssueSlaJob(IssueSlaService sla, ILogger<IssueSlaJob> logger)
{
    public const string Id = "issue-sla";

    /// <summary>Every 15 minutes, so an urgent issue's 4-hour target is flagged within 15 minutes of being missed.</summary>
    public const string DefaultCron = "*/15 * * * *";

    [AutomaticRetry(Attempts = 2)]
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var summary = await sla.RunAsync(cancellationToken);
        if (summary.Escalated > 0 || summary.AutoClosed > 0)
            logger.LogInformation("Issue SLA checks: {@Summary}", summary);
    }
}
