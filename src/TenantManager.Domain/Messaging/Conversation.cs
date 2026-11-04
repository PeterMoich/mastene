using TenantManager.Domain.Common;

namespace TenantManager.Domain.Messaging;

public enum MessageDirection
{
    /// <summary>From the tenant.</summary>
    Inbound,

    /// <summary>From the landlord or the system.</summary>
    Outbound,
}

public enum MessageChannel
{
    WhatsApp,

    /// <summary>Kept in the app only: written in the app by the tenant, or a landlord reply that couldn't go to WhatsApp.</summary>
    App,
}

public enum MessageKind
{
    Text,
    Template,
}

public enum MessageStatus
{
    /// <summary>Waiting to be sent to WhatsApp.</summary>
    Queued,
    Sent,
    Delivered,
    Read,
    Failed,

    /// <summary>An inbound message, or an outbound one kept in the app.</summary>
    Received,
}

/// <summary>
/// The message thread between the landlord and one tenant, across WhatsApp and the app. It remembers
/// when the tenant last wrote on WhatsApp, which opens Meta's 24-hour customer service window (BR17).
/// </summary>
public sealed class Conversation : Entity, IOwned
{
    public const int PreviewMax = 120;
    public static readonly TimeSpan ServiceWindow = TimeSpan.FromHours(24);

    private Conversation() { } // EF Core

    public Conversation(Guid ownerId, Guid tenantId)
    {
        OwnerId = ownerId;
        TenantId = tenantId;
    }

    public Guid OwnerId { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>The tenant's last WhatsApp message. Messages they write in the app don't count.</summary>
    public DateTimeOffset? LastWhatsAppInboundAt { get; private set; }

    public DateTimeOffset? LastMessageAt { get; private set; }
    public string? LastMessagePreview { get; private set; }
    public int UnreadByLandlord { get; private set; }
    public int UnreadByTenant { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>BR17: free-form WhatsApp messages are allowed until 24 hours after the tenant's last WhatsApp message.</summary>
    public bool InServiceWindow(DateTimeOffset now) =>
        LastWhatsAppInboundAt is { } last && now < last + ServiceWindow;

    public DateTimeOffset? ServiceWindowEndsAt => LastWhatsAppInboundAt + ServiceWindow;

    /// <summary>Called for every message added to the thread.</summary>
    public void Touch(Message message)
    {
        if (LastMessageAt is null || message.At >= LastMessageAt)
        {
            LastMessageAt = message.At;
            var body = message.Body.ReplaceLineEndings(" ");
            LastMessagePreview = body.Length > PreviewMax ? body[..(PreviewMax - 1)] + "…" : body;
        }
        if (message.Direction == MessageDirection.Inbound)
        {
            UnreadByLandlord++;
            if (message.Channel == MessageChannel.WhatsApp &&
                (LastWhatsAppInboundAt is null || message.At > LastWhatsAppInboundAt))
                LastWhatsAppInboundAt = message.At;
        }
        else
        {
            UnreadByTenant++;
        }
    }

    public void MarkReadByLandlord() => UnreadByLandlord = 0;

    public void Redact() => LastMessagePreview = Privacy.Removed;

    public void MarkReadByTenant() => UnreadByTenant = 0;
}

/// <summary>One message in a conversation. Outbound WhatsApp messages are queued, then sent by the dispatcher (an outbox).</summary>
public sealed class Message : Entity
{
    public const int BodyMax = 4096; // WhatsApp's limit for a text message
    public const int ErrorMax = 500;

    private Message() { } // EF Core

    private Message(Guid conversationId, MessageDirection direction, MessageChannel channel, MessageKind kind,
        string body, MessageStatus status, DateTimeOffset at, string? byName)
    {
        ConversationId = conversationId;
        Direction = direction;
        Channel = channel;
        Kind = kind;
        Body = Guard.NotBlank(body, "body", BodyMax);
        Status = status;
        At = at;
        ByName = byName;
    }

    /// <summary>A message the tenant sent on WhatsApp (from the webhook).</summary>
    public static Message InboundWhatsApp(Guid conversationId, string body, string providerMessageId, DateTimeOffset at,
        string tenantName) =>
        new(conversationId, MessageDirection.Inbound, MessageChannel.WhatsApp, MessageKind.Text, body,
            MessageStatus.Received, at, tenantName) { ProviderMessageId = providerMessageId };

    /// <summary>A message the tenant wrote in the app.</summary>
    public static Message InboundApp(Guid conversationId, string body, DateTimeOffset at, string tenantName) =>
        new(conversationId, MessageDirection.Inbound, MessageChannel.App, MessageKind.Text, body,
            MessageStatus.Received, at, tenantName);

    /// <summary>
    /// A landlord's free-form reply. BR17: it goes to WhatsApp only when the tenant has consented and is
    /// inside the 24-hour window; otherwise it is kept in the app, where the tenant can still read it.
    /// </summary>
    public static Message OutboundText(Guid conversationId, string body, bool toWhatsApp, DateTimeOffset at,
        string byName) =>
        new(conversationId, MessageDirection.Outbound, toWhatsApp ? MessageChannel.WhatsApp : MessageChannel.App,
            MessageKind.Text, body, toWhatsApp ? MessageStatus.Queued : MessageStatus.Received, at, byName);

    /// <summary>A template message for WhatsApp, always queued. The caller has checked consent (BR16).</summary>
    public static Message OutboundTemplate(Guid conversationId, RenderedTemplate template, DateTimeOffset at,
        string byName, string? dedupeKey) =>
        new(conversationId, MessageDirection.Outbound, MessageChannel.WhatsApp, MessageKind.Template, template.Body,
            MessageStatus.Queued, at, byName)
        {
            TemplateName = template.Name,
            TemplateParameters = string.Join('\u001f', template.Parameters),
            DedupeKey = dedupeKey,
        };

    public Guid ConversationId { get; private set; }
    public MessageDirection Direction { get; private set; }
    public MessageChannel Channel { get; private set; }
    public MessageKind Kind { get; private set; }

    /// <summary>The text as the tenant sees it (a template is stored filled in).</summary>
    public string Body { get; private set; } = string.Empty;

    public string? TemplateName { get; private set; }

    /// <summary>The template's parameter values, separated by U+001F, for sending.</summary>
    public string? TemplateParameters { get; private set; }

    public MessageStatus Status { get; private set; }

    /// <summary>WhatsApp's message id ("wamid..."), unique, so a webhook delivered twice is stored once.</summary>
    public string? ProviderMessageId { get; private set; }

    /// <summary>Set for automatic reminders and notifications, e.g. "rent-overdue:{chargeId}", unique, so each is sent once.</summary>
    public string? DedupeKey { get; private set; }

    public string? Error { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset? LastAttemptAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public DateTimeOffset At { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }

    /// <summary>Who wrote it: the landlord's or tenant's name, or "System" for reminders.</summary>
    public string? ByName { get; private set; }

    /// <summary>POPIA erasure: the text goes; that a message was sent, when and how, stays.</summary>
    public void Redact()
    {
        Body = Privacy.Removed;
        TemplateParameters = null;
        if (Direction == MessageDirection.Inbound) ByName = Privacy.FormerTenant;
    }

    public IReadOnlyList<string> Parameters =>
        TemplateParameters is null ? [] : TemplateParameters.Split('\u001f');

    /// <summary>
    /// Claims the message for one send attempt. Saved before calling WhatsApp, and the row version makes
    /// a second dispatcher's claim fail, so two dispatchers never send the same message.
    /// </summary>
    public void BeginAttempt(DateTimeOffset now)
    {
        if (Status != MessageStatus.Queued) throw new InvalidOperationException("Only a queued message is sent.");
        Attempts++;
        LastAttemptAt = now;
    }

    public void MarkSent(string providerMessageId, DateTimeOffset at)
    {
        if (Status != MessageStatus.Queued) throw new InvalidOperationException("Only a queued message is sent.");
        ProviderMessageId = providerMessageId;
        Status = MessageStatus.Sent;
        SentAt = at;
        Error = null;
    }

    /// <summary>A failed attempt. After <paramref name="maxAttempts"/> attempts it stops retrying.</summary>
    public void RecordFailure(string error, int maxAttempts)
    {
        if (Status != MessageStatus.Queued) throw new InvalidOperationException("Only a queued message is sent.");
        Error = error.Length > ErrorMax ? error[..ErrorMax] : error;
        if (Attempts >= maxAttempts) Status = MessageStatus.Failed;
    }

    /// <summary>Not sent after all, e.g. consent was withdrawn while it waited in the queue.</summary>
    public void Cancel(string reason)
    {
        if (Status != MessageStatus.Queued) return;
        Status = MessageStatus.Failed;
        Error = reason;
    }

    /// <summary>
    /// A status update from WhatsApp. Statuses only move forward (sent → delivered → read), because
    /// webhooks can arrive out of order; a failure is recorded whenever it arrives.
    /// </summary>
    public bool ApplyProviderStatus(MessageStatus status, string? error)
    {
        if (Direction != MessageDirection.Outbound || Channel != MessageChannel.WhatsApp) return false;
        if (status == MessageStatus.Failed)
        {
            if (Status == MessageStatus.Failed) return false;
            Status = MessageStatus.Failed;
            Error = error is { Length: > ErrorMax } ? error[..ErrorMax] : error ?? "WhatsApp could not deliver the message.";
            return true;
        }
        if (Rank(status) <= Rank(Status)) return false;
        Status = status;
        return true;
    }

    private static int Rank(MessageStatus s) => s switch
    {
        MessageStatus.Queued => 0,
        MessageStatus.Sent => 1,
        MessageStatus.Delivered => 2,
        MessageStatus.Read => 3,
        _ => 4, // Failed and Received are final
    };
}
