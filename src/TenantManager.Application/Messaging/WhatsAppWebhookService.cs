using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TenantManager.Application.Abstractions;
using TenantManager.Domain.Messaging;

namespace TenantManager.Application.Messaging;

/// <param name="SignatureValid">False means the call was refused and nothing was read (BR18).</param>
public sealed record WebhookResult(bool SignatureValid, int MessagesStored, int StatusesApplied);

/// <summary>
/// Handles Meta's webhook: tenants' WhatsApp messages and delivery statuses of ours. The body is only
/// read after its signature checks out (BR18). Every event is idempotent: Meta retries deliveries, so
/// a message id already stored is skipped.
/// </summary>
public sealed class WhatsAppWebhookService(
    IApplicationDbContext db, IWhatsAppConfig config, IClock clock, ILogger<WhatsAppWebhookService> logger)
{
    /// <summary>Replies that withdraw consent. LEGAL REVIEW NEEDED: whether other wording must count too.</summary>
    public static readonly IReadOnlySet<string> StopWords =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "STOP", "UNSUBSCRIBE", "STOP ALL" };

    public async Task<WebhookResult> HandleAsync(byte[] body, string? signature, CancellationToken ct)
    {
        if (!WhatsAppSignature.IsValid(body, signature, config.AppSecret))
        {
            logger.LogWarning("WhatsApp webhook refused: missing or wrong signature");
            AppTelemetry.WebhooksRejected.Add(1);
            return new WebhookResult(false, 0, 0);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            logger.LogWarning("WhatsApp webhook body is not JSON; ignored");
            return new WebhookResult(true, 0, 0);
        }

        var stored = 0;
        var applied = 0;
        using (doc)
        {
            foreach (var value in Values(doc.RootElement))
            {
                if (value.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                    foreach (var m in messages.EnumerateArray())
                        if (await StoreInboundAsync(m, ct)) stored++;
                if (value.TryGetProperty("statuses", out var statuses) && statuses.ValueKind == JsonValueKind.Array)
                    foreach (var s in statuses.EnumerateArray())
                        if (await ApplyStatusAsync(s, ct)) applied++;
            }
        }
        return new WebhookResult(true, stored, applied);
    }

    /// <summary>entry[].changes[].value, where Meta puts the messages and statuses.</summary>
    private static IEnumerable<JsonElement> Values(JsonElement root)
    {
        if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array) yield break;
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array) continue;
            foreach (var change in changes.EnumerateArray())
                if (change.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object)
                    yield return value;
        }
    }

    private async Task<bool> StoreInboundAsync(JsonElement m, CancellationToken ct)
    {
        var id = Text(m, "id");
        var from = Text(m, "from");
        if (id is null || from is null) return false;
        if (await db.Messages.AnyAsync(x => x.ProviderMessageId == id, ct)) return false; // a retried delivery

        var phone = "+" + new string(from.Where(char.IsAsciiDigit).ToArray());
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Phone == phone, ct);
        if (tenant is null)
        {
            // Not stored: a number we don't know isn't a tenant's, and we don't keep strangers' messages (POPIA).
            logger.LogInformation("WhatsApp message from an unknown number ignored");
            return false;
        }

        var type = Text(m, "type") ?? "text";
        var bodyText = type == "text" && m.TryGetProperty("text", out var t) ? Text(t, "body") : null;
        var shown = string.IsNullOrWhiteSpace(bodyText)
            ? $"[{type} message: open WhatsApp on the business phone to see it]"
            : bodyText.Length > Message.BodyMax ? bodyText[..Message.BodyMax] : bodyText;

        var conversation = await db.GetOrAddConversationAsync(tenant, ct);
        var message = Message.InboundWhatsApp(conversation.Id, shown, id, Timestamp(m), tenant.FullName);
        db.Messages.Add(message);
        conversation.Touch(message);

        if (bodyText is not null && StopWords.Contains(bodyText.Trim()))
        {
            var latest = await db.LatestConsentAsync(tenant.Id, ct);
            if (latest is { Granted: true })
                db.Consents.Add(ConsentRecord.Withdraw(tenant.OwnerId, tenant.Id, tenant.Phone, ConsentSource.WhatsAppStop,
                    tenant.FullName, null, "Replied STOP on WhatsApp", clock.UtcNow));
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Domain.Common.DomainRuleException ex) when (ex.Code == "duplicate")
        {
            return false; // the same delivery processed twice at once; the unique index kept one
        }
        return true;
    }

    private async Task<bool> ApplyStatusAsync(JsonElement s, CancellationToken ct)
    {
        var id = Text(s, "id");
        var status = Text(s, "status") switch
        {
            "sent" => MessageStatus.Sent,
            "delivered" => MessageStatus.Delivered,
            "read" => MessageStatus.Read,
            "failed" => MessageStatus.Failed,
            _ => (MessageStatus?)null,
        };
        if (id is null || status is null) return false;

        var message = await db.Messages.FirstOrDefaultAsync(x => x.ProviderMessageId == id, ct);
        if (message is null) return false;

        string? error = null;
        if (s.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            error = Text(errors[0], "title") ?? Text(errors[0], "message");
        if (!message.ApplyProviderStatus(status.Value, error)) return false;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Meta's unix timestamp, never later than now (a phone's clock can be wrong).</summary>
    private DateTimeOffset Timestamp(JsonElement m)
    {
        var now = clock.UtcNow;
        if (!long.TryParse(Text(m, "timestamp"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds > now.ToUnixTimeSeconds())
            return now;
        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
