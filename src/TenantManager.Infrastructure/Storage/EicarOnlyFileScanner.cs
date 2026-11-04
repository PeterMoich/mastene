using System.Text;
using TenantManager.Application.Files;

namespace TenantManager.Infrastructure.Storage;

/// <summary>
/// Development stand-in for a virus scanner (ADR-0005). It only recognises the EICAR test file, the
/// industry-standard harmless sample every scanner flags, so the "infected upload" path can be tested.
/// It does NOT protect against real malware: production must plug in ClamAV, Microsoft Defender for
/// Storage or similar behind <see cref="IFileScanner"/>.
/// </summary>
public sealed class EicarOnlyFileScanner : IFileScanner
{
    private static readonly byte[] Eicar =
        Encoding.ASCII.GetBytes("EICAR-STANDARD-ANTIVIRUS-TEST-FILE");

    public Task<bool> IsCleanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
        Task.FromResult(content.Span.IndexOf(Eicar) < 0);
}
