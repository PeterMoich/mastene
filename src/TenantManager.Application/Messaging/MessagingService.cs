using System.Text;
using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Common;
using TenantManager.Domain.Common;
using TenantManager.Domain.Messaging;
using TenantManager.Domain.Tenants;

namespace TenantManager.Application.Messaging;

public sealed record SendMessageRequest(string Body);

public sealed record SendTemplateRequest(string Name, IReadOnlyList<string> Parameters);

/// <param name="Granted">True to give consent, false to withdraw it.</param>
/// <param name="Note">Required when the landlord records consent: how it was given.</param>
public sealed record ConsentRequest(bool Granted, string? Note);

public sealed record SimulateInboundRequest(Guid TenantId, string Body);

public sealed record ConsentDto(bool Granted, DateTimeOffset At, ConsentSource Source, string RecordedByName, string TextVersion,
    string Phone, string? Note);

/// <param name="Granted">Whether the tenant may be messaged on WhatsApp now (BR16).</param>
/// <param name="Text">The wording to show before someone agrees.</param>
public sealed record ConsentStatusDto(bool Granted, string Phone, string TextVersion, string Text, ConsentDto? Latest,
    IReadOnlyList<ConsentDto> History);

public sealed record MessageDto(
    Guid Id, MessageDirection Direction, MessageChannel Channel, MessageKind Kind, string? TemplateName, string Body,
    MessageStatus Status, string? Error, DateTimeOffset At, DateTimeOffset? SentAt, string? ByName);

public sealed record ConversationSummaryDto(
    Guid TenantId, string TenantName, string Phone, DateTimeOffset? LastMessageAt, string? LastMessagePreview,
    int Unread, bool InServiceWindow, bool ConsentGranted);

/// <param name="WhatsAppTextAllowed">BR16 and BR17: a free-form reply now goes to WhatsApp (otherwise it stays in the app).</param>
/// <param name="SimulationAvailable">The fake provider is in use, so the landlord can play the tenant replying on WhatsApp.</param>
public sealed record ThreadDto(
    Guid TenantId, string TenantName, string Phone, ConsentStatusDto Consent, bool InServiceWindow,
    DateTimeOffset? ServiceWindowEndsAt, bool WhatsAppTextAllowed, bool SimulationAvailable,
    IReadOnlyList<MessageDto> Messages);

public sealed record TemplateDto(string Name, string Description, string Body, IReadOnlyList<string> ParameterNames, bool Manual);

public sealed class SendMessageRequestValidator : AbstractValidator<SendMessageRequest>
{
    public SendMessageRequestValidator() => RuleFor(x => x.Body).NotEmpty().MaximumLength(Message.BodyMax);
}

public sealed class SendTemplateRequestValidator : AbstractValidator<SendTemplateRequest>
{
    public SendTemplateRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Parameters).NotNull().Must(p => p.Count <= 10);
        RuleForEach(x => x.Parameters).NotEmpty().MaximumLength(MessageTemplate.ParameterMax);
    }
}

public sealed class ConsentRequestValidator : AbstractValidator<ConsentRequest>
{
    public ConsentRequestValidator() => RuleFor(x => x.Note).MaximumLength(ConsentRecord.NoteMax);
}

/// <summary>
/// Conversations with tenants across WhatsApp and the app, and their WhatsApp consent. A landlord sees
/// every thread; a tenant sees only their own (BR01).
/// </summary>
public sealed class MessagingService(
    IApplicationDbContext db,
    ICurrentUser user,
    IClock clock,
    IWhatsAppConfig config,
    MessageDispatcher dispatcher,
    WhatsAppWebhookService webhook,
    IValidator<SendMessageRequest> messageValidator,
    IValidator<SendTemplateRequest> templateValidator,
    IValidator<ConsentRequest> consentValidator)
{
    private const int ThreadLimit = 200;

    public static IReadOnlyList<TemplateDto> Templates() =>
        MessageTemplates.All.Select(t => new TemplateDto(t.Name, t.Description, t.Body, t.ParameterNames, t.Manual)).ToList();

    public async Task<IReadOnlyList<ConversationSummaryDto>> ConversationsAsync(CancellationToken ct)
    {
        if (user.IsTenant()) throw new UnauthorizedAccessException();
        var ownerId = user.RequireOwnerId();
        var rows = await db.Conversations.AsNoTracking().Where(c => c.OwnerId == ownerId)
            .Join(db.Tenants.IgnoreQueryFilters(), c => c.TenantId, t => t.Id, (c, t) => new { c, t })
            .OrderByDescending(x => x.c.UnreadByLandlord > 0).ThenByDescending(x => x.c.LastMessageAt)
            .Take(200).ToListAsync(ct);

        var tenantIds = rows.Select(r => r.t.Id).ToList();
        var consents = await LatestConsentsAsync(tenantIds, ct);
        var now = clock.UtcNow;
        return rows.Select(r => new ConversationSummaryDto(r.t.Id, r.t.FullName, r.t.Phone, r.c.LastMessageAt,
            r.c.LastMessagePreview, r.c.UnreadByLandlord, r.c.InServiceWindow(now),
            ConsentRecord.Allows(consents.GetValueOrDefault(r.t.Id), r.t.Phone))).ToList();
    }

    /// <summary>The thread (latest 200 messages, oldest first). Opening it marks it read for the viewer.</summary>
    public async Task<ThreadDto> ThreadAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await TenantAsync(tenantId, ct);
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);
        var messages = new List<MessageDto>();
        if (conversation is not null)
        {
            messages = (await db.Messages.AsNoTracking().Where(m => m.ConversationId == conversation.Id)
                    .OrderByDescending(m => m.At).Take(ThreadLimit).ToListAsync(ct))
                .OrderBy(m => m.At).Select(ToDto).ToList();
            var wasUnread = user.IsTenant() ? conversation.UnreadByTenant > 0 : conversation.UnreadByLandlord > 0;
            if (wasUnread)
            {
                if (user.IsTenant()) conversation.MarkReadByTenant();
                else conversation.MarkReadByLandlord();
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DomainRuleException ex) when (ex.Code == "concurrency")
                {
                    // A message arrived at the same moment; it stays unread, which is right.
                }
            }
        }

        var consent = await ConsentStatusAsync(tenant, ct);
        var now = clock.UtcNow;
        var inWindow = conversation?.InServiceWindow(now) ?? false;
        return new ThreadDto(tenant.Id, tenant.FullName, tenant.Phone, consent, inWindow,
            inWindow ? conversation!.ServiceWindowEndsAt : null, inWindow && consent.Granted,
            config.IsFake && !user.IsTenant(), messages);
    }

    /// <summary>
    /// The landlord's free-form reply goes to WhatsApp only with consent and inside the 24-hour window
    /// (BR16, BR17); otherwise it is kept in the app. A tenant's message written here is kept in the app.
    /// </summary>
    public async Task<ThreadDto> SendAsync(Guid tenantId, SendMessageRequest request, CancellationToken ct)
    {
        await messageValidator.ValidateAndThrowAsync(request, ct);
        var tenant = await TenantAsync(tenantId, ct);
        var conversation = await db.GetOrAddConversationAsync(tenant, ct);
        var now = clock.UtcNow;
        Message message;
        if (user.IsTenant())
        {
            message = Message.InboundApp(conversation.Id, request.Body, now, tenant.FullName);
        }
        else
        {
            var toWhatsApp = conversation.InServiceWindow(now) &&
                             ConsentRecord.Allows(await db.LatestConsentAsync(tenant.Id, ct), tenant.Phone);
            message = Message.OutboundText(conversation.Id, request.Body, toWhatsApp, now, user.DisplayName ?? "Landlord");
        }
        db.Messages.Add(message);
        conversation.Touch(message);
        await db.SaveChangesAsync(ct);
        await dispatcher.TryDispatchAsync(ct);
        return await ThreadAsync(tenantId, ct);
    }

    /// <summary>A template the landlord chose, e.g. to reach the tenant outside the 24-hour window. Needs consent (BR16).</summary>
    public async Task<ThreadDto> SendTemplateAsync(Guid tenantId, SendTemplateRequest request, CancellationToken ct)
    {
        if (user.IsTenant()) throw new UnauthorizedAccessException();
        await templateValidator.ValidateAndThrowAsync(request, ct);
        var template = MessageTemplates.Find(request.Name);
        if (!template.Manual)
            throw new DomainRuleException("template.automatic", "That template is sent automatically and can't be sent by hand.");
        var tenant = await TenantAsync(tenantId, ct);
        if (!ConsentRecord.Allows(await db.LatestConsentAsync(tenant.Id, ct), tenant.Phone))
            throw new DomainRuleException("consent.required",
                "This tenant hasn't agreed to WhatsApp messages on their current number.", RuleKind.Conflict);

        var conversation = await db.GetOrAddConversationAsync(tenant, ct);
        var message = Message.OutboundTemplate(conversation.Id, template.Render(request.Parameters), clock.UtcNow,
            user.DisplayName ?? "Landlord", dedupeKey: null);
        db.Messages.Add(message);
        conversation.Touch(message);
        await db.SaveChangesAsync(ct);
        await dispatcher.TryDispatchAsync(ct);
        return await ThreadAsync(tenantId, ct);
    }

    public async Task<ConsentStatusDto> GetConsentAsync(Guid tenantId, CancellationToken ct) =>
        await ConsentStatusAsync(await TenantAsync(tenantId, ct), ct);

    /// <summary>
    /// Grants or withdraws WhatsApp consent. The tenant does this for themself; the landlord can record
    /// consent given another way (with a note saying how) or a withdrawal.
    /// </summary>
    public async Task<ConsentStatusDto> SetConsentAsync(Guid tenantId, ConsentRequest request, CancellationToken ct)
    {
        await consentValidator.ValidateAndThrowAsync(request, ct);
        var tenant = await TenantAsync(tenantId, ct);
        var source = user.IsTenant() ? ConsentSource.Tenant : ConsentSource.Landlord;
        var name = user.DisplayName ?? (user.IsTenant() ? tenant.FullName : "Landlord");
        var record = request.Granted
            ? ConsentRecord.Grant(tenant.OwnerId, tenant.Id, tenant.Phone, source, name, user.UserId, request.Note, clock.UtcNow)
            : ConsentRecord.Withdraw(tenant.OwnerId, tenant.Id, tenant.Phone, source, name, user.UserId, request.Note, clock.UtcNow);
        db.Consents.Add(record);
        await db.SaveChangesAsync(ct);
        return await ConsentStatusAsync(tenant, ct);
    }

    /// <summary>
    /// Development only (fake provider): plays the tenant replying on WhatsApp. Builds the same signed
    /// payload Meta would send and passes it through the real webhook handler, signature check included.
    /// </summary>
    public async Task<ThreadDto> SimulateInboundAsync(SimulateInboundRequest request, CancellationToken ct)
    {
        if (!config.IsFake)
            throw new DomainRuleException("simulation.unavailable", "Simulated replies only work with the fake WhatsApp provider.",
                RuleKind.Conflict);
        await messageValidator.ValidateAndThrowAsync(new SendMessageRequest(request.Body), ct);
        if (user.IsTenant()) throw new UnauthorizedAccessException();
        var tenant = await TenantAsync(request.TenantId, ct);

        var payload = new
        {
            @object = "whatsapp_business_account",
            entry = new[]
            {
                new
                {
                    id = "SIMULATED",
                    changes = new[]
                    {
                        new
                        {
                            field = "messages",
                            value = new
                            {
                                messaging_product = "whatsapp",
                                messages = new[]
                                {
                                    new
                                    {
                                        from = tenant.Phone.TrimStart('+'),
                                        id = $"wamid.SIM{Guid.NewGuid():N}",
                                        timestamp = clock.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                                        type = "text",
                                        text = new { body = request.Body },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await webhook.HandleAsync(body, WhatsAppSignature.Sign(body, config.AppSecret), ct);
        return await ThreadAsync(tenant.Id, ct);
    }

    private async Task<Tenant> TenantAsync(Guid tenantId, CancellationToken ct)
    {
        if (user.IsTenant())
        {
            if (user.TenantId != tenantId) throw new NotFoundException("Tenant", tenantId);
            return await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct) ?? throw new NotFoundException("Tenant", tenantId);
        }
        var ownerId = user.RequireOwnerId();
        return await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId && t.OwnerId == ownerId, ct)
               ?? throw new NotFoundException("Tenant", tenantId);
    }

    private async Task<ConsentStatusDto> ConsentStatusAsync(Tenant tenant, CancellationToken ct)
    {
        var history = (await db.Consents.AsNoTracking().Where(c => c.TenantId == tenant.Id)
                .OrderByDescending(c => c.At).ThenByDescending(c => c.CreatedAt).Take(50).ToListAsync(ct))
            .Select(c => new ConsentDto(c.Granted, c.At, c.Source, c.RecordedByName, c.TextVersion, c.Phone, c.Note)).ToList();
        var latest = history.FirstOrDefault();
        var granted = latest is { Granted: true } && latest.Phone == tenant.Phone;
        return new ConsentStatusDto(granted, tenant.Phone, ConsentText.CurrentVersion, ConsentText.Current, latest, history);
    }

    private async Task<Dictionary<Guid, ConsentRecord>> LatestConsentsAsync(IReadOnlyCollection<Guid> tenantIds,
        CancellationToken ct) =>
        (await db.Consents.AsNoTracking().Where(c => tenantIds.Contains(c.TenantId)).ToListAsync(ct))
        .GroupBy(c => c.TenantId)
        .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.At).ThenByDescending(c => c.CreatedAt).First());

    private static MessageDto ToDto(Message m) =>
        new(m.Id, m.Direction, m.Channel, m.Kind, m.TemplateName, m.Body, m.Status, m.Error, m.At, m.SentAt, m.ByName);
}
