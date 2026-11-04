using System.Security.Cryptography;
using System.Text;

namespace TenantManager.Application.Messaging;

/// <summary>What a provider returned for one send.</summary>
public sealed record WhatsAppSendResult(bool Ok, string? ProviderMessageId, string? Error)
{
    public static WhatsAppSendResult Sent(string id) => new(true, id, null);
    public static WhatsAppSendResult Failed(string error) => new(false, null, error);
}

/// <summary>
/// Sends WhatsApp messages (ADR-0006). The fake provider is the default; the Meta Cloud API provider is
/// switched on by configuration. Numbers are E.164 with a leading '+'.
/// </summary>
public interface IWhatsAppProvider
{
    Task<WhatsAppSendResult> SendTextAsync(string toE164, string body, CancellationToken cancellationToken);

    Task<WhatsAppSendResult> SendTemplateAsync(string toE164, string templateName, string language,
        IReadOnlyList<string> parameters, CancellationToken cancellationToken);
}

/// <summary>The WhatsApp settings the application needs. Secrets come from configuration, never from code.</summary>
public interface IWhatsAppConfig
{
    /// <summary>The Meta app secret that signs webhook calls (BR18).</summary>
    string AppSecret { get; }

    /// <summary>The token Meta echoes when the webhook is first registered.</summary>
    string VerifyToken { get; }

    /// <summary>True for the fake provider: inbound messages can then be simulated.</summary>
    bool IsFake { get; }
}

/// <summary>BR18: Meta signs every webhook body with HMAC-SHA256 using the app secret, in X-Hub-Signature-256.</summary>
public static class WhatsAppSignature
{
    public const string Header = "X-Hub-Signature-256";

    public static string Sign(ReadOnlySpan<byte> body, string secret) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    /// <summary>Constant-time comparison, so the signature can't be guessed byte by byte from response times.</summary>
    public static bool IsValid(ReadOnlySpan<byte> body, string? header, string secret)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(header) ||
            !header.StartsWith("sha256=", StringComparison.Ordinal))
            return false;
        byte[] given;
        try
        {
            given = Convert.FromHexString(header.AsSpan(7));
        }
        catch (FormatException)
        {
            return false;
        }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }

    public static bool TokenMatches(string? given, string expected) =>
        !string.IsNullOrEmpty(expected) && given is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));
}
