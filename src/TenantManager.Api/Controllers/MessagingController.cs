using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TenantManager.Api.Infrastructure;
using TenantManager.Application.Messaging;

namespace TenantManager.Api.Controllers;

/// <summary>
/// Conversations with tenants (WhatsApp and in-app) and WhatsApp consent. Landlords see every thread;
/// a tenant sees only their own.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
public sealed class MessagingController(MessagingService messaging) : ControllerBase
{
    /// <summary>Every thread, unread first, then newest.</summary>
    [HttpGet("conversations")]
    [Authorize(Policy = Policies.Landlord)]
    public Task<IReadOnlyList<ConversationSummaryDto>> Conversations(CancellationToken ct) => messaging.ConversationsAsync(ct);

    /// <summary>The thread with one tenant (the latest 200 messages), their consent and the 24-hour window. Marks it read.</summary>
    [HttpGet("conversations/{tenantId:guid}")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public Task<ThreadDto> Thread(Guid tenantId, CancellationToken ct) => messaging.ThreadAsync(tenantId, ct);

    /// <summary>
    /// A free-form message. From the landlord it goes to WhatsApp only with consent and inside the
    /// 24-hour window (BR16, BR17), otherwise it is kept in the app. From a tenant it is kept in the app.
    /// </summary>
    [HttpPost("conversations/{tenantId:guid}/messages")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public Task<ThreadDto> Send(Guid tenantId, SendMessageRequest request, CancellationToken ct) =>
        messaging.SendAsync(tenantId, request, ct);

    /// <summary>Sends a template by hand (works outside the 24-hour window). Needs the tenant's consent.</summary>
    [HttpPost("conversations/{tenantId:guid}/templates")]
    [Authorize(Policy = Policies.Landlord)]
    public Task<ThreadDto> SendTemplate(Guid tenantId, SendTemplateRequest request, CancellationToken ct) =>
        messaging.SendTemplateAsync(tenantId, request, ct);

    /// <summary>The WhatsApp templates the app uses; those marked manual can be sent by hand.</summary>
    [HttpGet("message-templates")]
    [Authorize(Policy = Policies.Landlord)]
    public IReadOnlyList<TemplateDto> Templates() => MessagingService.Templates();

    /// <summary>WhatsApp consent: whether it is given now, the wording, and the full history.</summary>
    [HttpGet("tenants/{tenantId:guid}/consents")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public Task<ConsentStatusDto> Consent(Guid tenantId, CancellationToken ct) => messaging.GetConsentAsync(tenantId, ct);

    /// <summary>Gives or withdraws consent. The landlord must say how a tenant gave consent.</summary>
    [HttpPost("tenants/{tenantId:guid}/consents")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public Task<ConsentStatusDto> SetConsent(Guid tenantId, ConsentRequest request, CancellationToken ct) =>
        messaging.SetConsentAsync(tenantId, request, ct);

    /// <summary>Fake WhatsApp only: plays the tenant replying on WhatsApp, through the real signed webhook path.</summary>
    [HttpPost("dev/whatsapp/inbound")]
    [Authorize(Policy = Policies.Landlord)]
    public Task<ThreadDto> SimulateInbound(SimulateInboundRequest request, CancellationToken ct) =>
        messaging.SimulateInboundAsync(request, ct);
}

/// <summary>
/// Meta's WhatsApp webhook. Public, because Meta calls it, but every POST must carry a valid
/// X-Hub-Signature-256 made with the app secret (BR18).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/webhooks/whatsapp")]
[AllowAnonymous]
public sealed class WhatsAppWebhookController(WhatsAppWebhookService webhook, IWhatsAppConfig config) : ControllerBase
{
    private const int MaxBody = 1024 * 1024;

    /// <summary>Meta's one-time check when the webhook is registered: echoes hub.challenge if the verify token matches.</summary>
    [HttpGet]
    public IActionResult Verify([FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? token, [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        if (mode != "subscribe" || !WhatsAppSignature.TokenMatches(token, config.VerifyToken) || string.IsNullOrEmpty(challenge))
            return StatusCode(StatusCodes.Status403Forbidden);
        return Content(challenge, "text/plain");
    }

    /// <summary>Inbound messages and delivery statuses. 401 when the signature is missing or wrong.</summary>
    [HttpPost]
    [RequestSizeLimit(MaxBody)]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, ct);
        var result = await webhook.HandleAsync(buffer.ToArray(), Request.Headers[WhatsAppSignature.Header], ct);
        return result.SignatureValid ? Ok() : Unauthorized();
    }
}
