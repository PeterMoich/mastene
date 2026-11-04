using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TenantManager.Application.Abstractions;
using TenantManager.Domain.Common;
using TenantManager.Domain.Messaging;

namespace TenantManager.Application.Messaging;

/// <summary>
/// Sends queued WhatsApp messages (the outbox). Runs every minute in Hangfire, and straight after a
/// request queues something so replies go out at once. Consent and the 24-hour window are checked
/// again at send time, because either can change while a message waits (BR16, BR17).
/// </summary>
public sealed class MessageDispatcher(
    IApplicationDbContext db, IWhatsAppProvider provider, IClock clock, ILogger<MessageDispatcher> logger)
{
    public const int MaxAttempts = 5;
    private const int Batch = 50;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    /// <summary>Never throws: a failure to send must not fail the request that queued the message.</summary>
    public async Task<int> TryDispatchAsync(CancellationToken ct)
    {
        try
        {
            return await DispatchAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Sending queued WhatsApp messages failed; the next run will retry");
            return 0;
        }
    }

    /// <returns>How many messages were sent.</returns>
    public async Task<int> DispatchAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var retryBefore = now - RetryAfter;
        var queued = await db.Messages
            .Where(m => m.Status == MessageStatus.Queued && m.Channel == MessageChannel.WhatsApp &&
                        (m.LastAttemptAt == null || m.LastAttemptAt < retryBefore))
            .OrderBy(m => m.At).Take(Batch).ToListAsync(ct);
        if (queued.Count == 0) return 0;

        var conversationIds = queued.Select(m => m.ConversationId).Distinct().ToList();
        var conversations = await db.Conversations.Where(c => conversationIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var tenantIds = conversations.Values.Select(c => c.TenantId).ToList();
        var tenants = await db.Tenants.AsNoTracking().Where(t => tenantIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

        var sent = 0;
        foreach (var message in queued)
        {
            var conversation = conversations[message.ConversationId];
            if (!tenants.TryGetValue(conversation.TenantId, out var tenant))
            {
                message.Cancel("The tenant has been deleted.");
                await db.SaveChangesAsync(ct);
                continue;
            }
            if (!ConsentRecord.Allows(await db.LatestConsentAsync(tenant.Id, ct), tenant.Phone))
            {
                message.Cancel("Not sent: the tenant hasn't agreed to WhatsApp messages on this number.");
                await db.SaveChangesAsync(ct);
                continue;
            }
            if (message.Kind == MessageKind.Text && !conversation.InServiceWindow(clock.UtcNow))
            {
                message.Cancel("Not sent: the 24-hour window closed before it could be sent. Use a template.");
                await db.SaveChangesAsync(ct);
                continue;
            }

            // Claim it first. If another dispatcher claimed it meanwhile, the row version check fails here.
            message.BeginAttempt(clock.UtcNow);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DomainRuleException ex) when (ex.Code == "concurrency")
            {
                logger.LogInformation("Another dispatcher is sending; stopping this run");
                return sent;
            }

            var result = message.Kind == MessageKind.Template
                ? await provider.SendTemplateAsync(tenant.Phone, message.TemplateName!, MessageTemplates.Language,
                    message.Parameters, ct)
                : await provider.SendTextAsync(tenant.Phone, message.Body, ct);

            if (result.Ok)
            {
                message.MarkSent(result.ProviderMessageId!, clock.UtcNow);
                sent++;
            }
            else
            {
                message.RecordFailure(result.Error ?? "WhatsApp refused the message.", MaxAttempts);
                logger.LogWarning("WhatsApp send failed for message {MessageId} (attempt {Attempt}): {Error}",
                    message.Id, message.Attempts, result.Error);
            }
            await db.SaveChangesAsync(ct);
        }
        return sent;
    }
}
