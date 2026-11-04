using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenantManager.Application.Messaging;

namespace TenantManager.Infrastructure.Messaging;

public sealed class WhatsAppOptions
{
    public const string Section = "WhatsApp";

    /// <summary>"Fake" (default) or "Meta".</summary>
    public string Provider { get; set; } = "Fake";

    /// <summary>Meta app secret, used to check webhook signatures. Set from the environment, never committed.</summary>
    public string? AppSecret { get; set; }

    /// <summary>The token entered in Meta's webhook settings, echoed back once to prove we own the URL.</summary>
    public string? VerifyToken { get; set; }

    /// <summary>Meta: a system-user access token for the WhatsApp Business account.</summary>
    public string? AccessToken { get; set; }

    /// <summary>Meta: the id of the business phone number that sends messages.</summary>
    public string? PhoneNumberId { get; set; }

    public string GraphApiVersion { get; set; } = "v21.0";

    public bool IsFake => string.Equals(Provider, "Fake", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The WhatsApp settings for the application. With the fake provider and no secret configured, a random
/// secret is made for this run, so simulated replies are still signed and checked.
/// </summary>
public sealed class WhatsAppConfig : IWhatsAppConfig
{
    public WhatsAppConfig(IOptions<WhatsAppOptions> options)
    {
        var o = options.Value;
        IsFake = o.IsFake;
        if (!IsFake && !string.Equals(o.Provider, "Meta", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unknown WhatsApp:Provider '{o.Provider}'. Use Fake or Meta.");
        if (!IsFake && (string.IsNullOrWhiteSpace(o.AppSecret) || string.IsNullOrWhiteSpace(o.VerifyToken) ||
                        string.IsNullOrWhiteSpace(o.AccessToken) || string.IsNullOrWhiteSpace(o.PhoneNumberId)))
            throw new InvalidOperationException(
                "WhatsApp:Provider is Meta but AppSecret, VerifyToken, AccessToken or PhoneNumberId is missing.");
        AppSecret = string.IsNullOrWhiteSpace(o.AppSecret) ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) : o.AppSecret;
        VerifyToken = o.VerifyToken ?? string.Empty;
    }

    public string AppSecret { get; }
    public string VerifyToken { get; }
    public bool IsFake { get; }
}

/// <summary>
/// Pretends to be WhatsApp (ADR-0006): every send succeeds with a made-up message id, except to numbers
/// ending in 0000, which fail the way a number that isn't on WhatsApp would, so failures can be tried.
/// The messages themselves are already stored by the app; this only logs them.
/// </summary>
public sealed class FakeWhatsAppProvider(ILogger<FakeWhatsAppProvider> logger) : IWhatsAppProvider
{
    public Task<WhatsAppSendResult> SendTextAsync(string toE164, string body, CancellationToken cancellationToken) =>
        Task.FromResult(Send(toE164, "text"));

    public Task<WhatsAppSendResult> SendTemplateAsync(string toE164, string templateName, string language,
        IReadOnlyList<string> parameters, CancellationToken cancellationToken) =>
        Task.FromResult(Send(toE164, $"template {templateName}"));

    private WhatsAppSendResult Send(string to, string what)
    {
        if (to.EndsWith("0000", StringComparison.Ordinal))
            return WhatsAppSendResult.Failed("(fake) 131026: this number is not on WhatsApp.");
        logger.LogInformation("Fake WhatsApp: {What} to a tenant's number", what);
        return WhatsAppSendResult.Sent($"wamid.FAKE{Guid.NewGuid():N}");
    }
}

/// <summary>
/// The WhatsApp Business Cloud API (graph.facebook.com/{version}/{phone-number-id}/messages). Switched on
/// with WhatsApp:Provider = Meta. Templates must already be approved in Meta's WhatsApp Manager.
/// </summary>
public sealed class MetaCloudWhatsAppProvider(HttpClient http, IOptions<WhatsAppOptions> options,
    ILogger<MetaCloudWhatsAppProvider> logger) : IWhatsAppProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Task<WhatsAppSendResult> SendTextAsync(string toE164, string body, CancellationToken cancellationToken) =>
        PostAsync(new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = Digits(toE164),
            type = "text",
            text = new { preview_url = false, body },
        }, cancellationToken);

    public Task<WhatsAppSendResult> SendTemplateAsync(string toE164, string templateName, string language,
        IReadOnlyList<string> parameters, CancellationToken cancellationToken) =>
        PostAsync(new
        {
            messaging_product = "whatsapp",
            to = Digits(toE164),
            type = "template",
            template = new
            {
                name = templateName,
                language = new { code = language },
                components = parameters.Count == 0 ? null : new[]
                {
                    new { type = "body", parameters = parameters.Select(p => new { type = "text", text = p }).ToArray() },
                },
            },
        }, cancellationToken);

    private async Task<WhatsAppSendResult> PostAsync(object payload, CancellationToken ct)
    {
        var o = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.facebook.com/{o.GraphApiVersion}/{o.PhoneNumberId}/messages")
        {
            Content = JsonContent.Create(payload, options: Json),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", o.AccessToken);
        try
        {
            using var response = await http.SendAsync(request, ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (response.IsSuccessStatusCode &&
                doc.RootElement.TryGetProperty("messages", out var messages) && messages.GetArrayLength() > 0 &&
                messages[0].TryGetProperty("id", out var id) && id.GetString() is { } messageId)
                return WhatsAppSendResult.Sent(messageId);

            var error = doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)
                ? $"{(e.TryGetProperty("code", out var c) ? c.ToString() + ": " : "")}{m.GetString()}"
                : $"WhatsApp returned {(int)response.StatusCode}.";
            return WhatsAppSendResult.Failed(error);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Calling the WhatsApp Cloud API failed");
            return WhatsAppSendResult.Failed("Could not reach WhatsApp: " + ex.Message);
        }
    }

    private static string Digits(string e164) => new(e164.Where(char.IsAsciiDigit).ToArray());
}
