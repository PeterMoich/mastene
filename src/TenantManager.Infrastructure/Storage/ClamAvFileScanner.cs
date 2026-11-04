using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenantManager.Application.Files;
using TenantManager.Domain.Common;

namespace TenantManager.Infrastructure.Storage;

public sealed class ScanningOptions
{
    public const string Section = "Scanning";

    /// <summary>"Eicar" (development only, the default) or "ClamAv".</summary>
    public string Provider { get; set; } = "Eicar";

    public string ClamAvHost { get; set; } = "clamav";
    public int ClamAvPort { get; set; } = 3310;
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Scans uploads with ClamAV's clamd over TCP (the INSTREAM command), ADR-0005. Fails closed: if clamd
/// can't be reached the upload is refused, never stored unscanned.
/// </summary>
public sealed class ClamAvFileScanner(IOptions<ScanningOptions> options, ILogger<ClamAvFileScanner> logger) : IFileScanner
{
    private const int Chunk = 64 * 1024;

    public async Task<bool> IsCleanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var o = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
        string reply;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(o.ClamAvHost, o.ClamAvPort, timeout.Token);
            await using var stream = client.GetStream();
            reply = await ScanAsync(stream, content, timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "The virus scanner (clamd at {Host}:{Port}) could not be reached", o.ClamAvHost, o.ClamAvPort);
            throw Unavailable();
        }

        if (reply.EndsWith("OK", StringComparison.Ordinal)) return true;
        if (reply.EndsWith("FOUND", StringComparison.Ordinal))
        {
            logger.LogWarning("Upload refused by the virus scanner: {Reply}", reply);
            return false;
        }
        logger.LogError("Unexpected reply from clamd: {Reply}", reply);
        throw Unavailable();
    }

    /// <summary>The clamd INSTREAM protocol: "zINSTREAM\0", length-prefixed chunks, a zero length, then one reply.</summary>
    internal static async Task<string> ScanAsync(Stream stream, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), ct);
        var size = new byte[4];
        for (var offset = 0; offset < content.Length; offset += Chunk)
        {
            var part = content.Slice(offset, Math.Min(Chunk, content.Length - offset));
            BinaryPrimitives.WriteUInt32BigEndian(size, (uint)part.Length);
            await stream.WriteAsync(size, ct);
            await stream.WriteAsync(part, ct);
        }
        BinaryPrimitives.WriteUInt32BigEndian(size, 0);
        await stream.WriteAsync(size, ct);
        await stream.FlushAsync(ct);

        using var reply = new MemoryStream();
        var buffer = new byte[256];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            var zero = Array.IndexOf(buffer, (byte)0, 0, read);
            reply.Write(buffer, 0, zero >= 0 ? zero : read);
            if (zero >= 0) break;
        }
        return Encoding.ASCII.GetString(reply.ToArray()).Trim(); // e.g. "stream: OK" or "stream: Eicar-Signature FOUND"
    }

    private static DomainRuleException Unavailable() =>
        new("file.scan_unavailable", "The virus scanner isn't available right now, so the file wasn't saved. Please try again later.",
            RuleKind.Conflict);
}
