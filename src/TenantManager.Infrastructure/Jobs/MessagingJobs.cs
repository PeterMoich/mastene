using Hangfire;
using Microsoft.Extensions.Logging;
using TenantManager.Application.Messaging;

namespace TenantManager.Infrastructure.Jobs;

/// <summary>Sends queued WhatsApp messages every minute (the outbox), retrying failures.</summary>
public sealed class MessageDispatchJob(MessageDispatcher dispatcher)
{
    public const string Id = "messages-dispatch";
    public const string DefaultCron = "* * * * *";

    [AutomaticRetry(Attempts = 0)] // the next run retries anyway
    [DisableConcurrentExecution(timeoutInSeconds: 120)]
    public Task RunAsync(CancellationToken cancellationToken) => dispatcher.DispatchAsync(cancellationToken);
}

/// <summary>Queues the daily WhatsApp reminders in the morning, then sends them.</summary>
public sealed class ReminderJob(ReminderService reminders, MessageDispatcher dispatcher, ILogger<ReminderJob> logger)
{
    public const string Id = "reminders";

    /// <summary>08:50 South African time, so reminders arrive at the start of the day, not at midnight.</summary>
    public const string DefaultCron = "50 8 * * *";

    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var summary = await reminders.RunAsync(cancellationToken);
        var sent = await dispatcher.DispatchAsync(cancellationToken);
        logger.LogInformation("Reminders queued: {@Summary}; messages sent: {Sent}", summary, sent);
    }
}
