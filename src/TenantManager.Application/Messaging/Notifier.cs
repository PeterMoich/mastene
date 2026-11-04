using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TenantManager.Application.Abstractions;
using TenantManager.Domain.Messaging;
using TenantManager.Domain.Tenants;

namespace TenantManager.Application.Messaging;

/// <summary>How values appear in messages: "R6,500.00", "1 November 2026", "November 2026".</summary>
public static class MessageFormat
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");

    public static string Money(decimal amount) => "R" + amount.ToString("N2", CultureInfo.InvariantCulture);

    public static string Date(DateOnly date) => date.ToString("d MMMM yyyy", En);

    public static string Month(string period) =>
        DateOnly.ParseExact(period + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("MMMM yyyy", En);
}

public static class ConversationAccess
{
    /// <summary>The thread for a tenant, created on first use. Finds one added earlier in the same unit of work too.</summary>
    public static async Task<Conversation> GetOrAddConversationAsync(this IApplicationDbContext db, Tenant tenant,
        CancellationToken ct)
    {
        var conversation = db.Conversations.Local.FirstOrDefault(c => c.TenantId == tenant.Id)
                           ?? await db.Conversations.FirstOrDefaultAsync(c => c.TenantId == tenant.Id, ct);
        if (conversation is null)
        {
            conversation = new Conversation(tenant.OwnerId, tenant.Id);
            db.Conversations.Add(conversation);
        }
        return conversation;
    }

    public static async Task<ConsentRecord?> LatestConsentAsync(this IApplicationDbContext db, Guid tenantId,
        CancellationToken ct) =>
        db.Consents.Local.Where(c => c.TenantId == tenantId).OrderByDescending(c => c.At).FirstOrDefault()
        ?? await db.Consents.AsNoTracking().Where(c => c.TenantId == tenantId).OrderByDescending(c => c.At)
            .ThenByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);
}

/// <summary>
/// Queues automatic WhatsApp messages (reminders and updates) in the same unit of work as the change
/// that caused them, so a message is queued exactly when the change is saved (an outbox). Nothing is
/// queued for a tenant without consent (BR16). The caller saves; the dispatcher sends.
/// </summary>
public sealed class Notifier(IApplicationDbContext db, IClock clock, ILogger<Notifier> logger)
{
    public const string SystemName = "System";

    /// <returns>True when a message was queued.</returns>
    public Task<bool> QueueAsync(Guid tenantId, MessageTemplate template, IReadOnlyList<string> values,
        string dedupeKey, CancellationToken ct) => QueueAsync(tenantId, template, _ => values, dedupeKey, ct);

    /// <param name="values">The template's values, given the tenant (usually for their first name).</param>
    public async Task<bool> QueueAsync(Guid tenantId, MessageTemplate template, Func<Tenant, IReadOnlyList<string>> values,
        string dedupeKey, CancellationToken ct)
    {
        if (db.Messages.Local.Any(m => m.DedupeKey == dedupeKey) ||
            await db.Messages.AnyAsync(m => m.DedupeKey == dedupeKey, ct))
            return false;

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return false; // deleted tenants aren't messaged
        if (!ConsentRecord.Allows(await db.LatestConsentAsync(tenantId, ct), tenant.Phone))
        {
            logger.LogDebug("Not queuing {Template} for tenant {TenantId}: no WhatsApp consent", template.Name, tenantId);
            return false;
        }

        var conversation = await db.GetOrAddConversationAsync(tenant, ct);
        var message = Message.OutboundTemplate(conversation.Id, template.Render(values(tenant)), clock.UtcNow, SystemName, dedupeKey);
        db.Messages.Add(message);
        conversation.Touch(message);
        return true;
    }
}
