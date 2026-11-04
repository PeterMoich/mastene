using System.Security.Cryptography;
using System.Text.RegularExpressions;
using TenantManager.Domain.Common;

namespace TenantManager.Application.Files;

/// <summary>A file as it arrived in the request. The name and declared type come from the client and aren't trusted.</summary>
public sealed record UploadedFile(string FileName, long Length, Stream Content);

public enum FileType
{
    Pdf,
    Jpeg,
    Png,
}

public sealed record UploadRules(IReadOnlyList<FileType> Allowed, long MaxBytes)
{
    public const long MiB = 1024 * 1024;

    /// <summary>BR12: PDF, JPG or PNG, up to 5 MB.</summary>
    public static UploadRules ProofOfPayment { get; } = new([FileType.Pdf, FileType.Jpeg, FileType.Png], 5 * MiB);

    /// <summary>BR12: JPG or PNG, up to 5 MB each (Phase 4).</summary>
    public static UploadRules IssuePhoto { get; } = new([FileType.Jpeg, FileType.Png], 5 * MiB);
}

/// <summary>An upload that passed every check, read fully into memory (it is at most a few MB).</summary>
public sealed record ValidatedUpload(byte[] Content, FileType Type, string ContentType, string Extension,
    string SafeFileName, string Sha256);

/// <summary>
/// Virus-scan hook (ADR-0005). Development uses a scanner that only recognises the EICAR test file so
/// the rejection path can be tested; production would plug in ClamAV or Microsoft Defender here.
/// </summary>
public interface IFileScanner
{
    Task<bool> IsCleanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
}

/// <summary>
/// Checks uploads on the server (BR12): size, then the real type from the file's first bytes (not its
/// name or the browser's claim), then the virus scan. A renamed .exe is refused because its bytes
/// aren't a PDF, JPEG or PNG.
/// </summary>
public sealed partial class UploadValidator(IFileScanner scanner)
{
    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];

    public async Task<ValidatedUpload> ValidateAsync(UploadedFile file, UploadRules rules, CancellationToken ct)
    {
        if (file.Length > rules.MaxBytes)
            throw TooLarge(rules);

        // Read at most one byte more than allowed, so a lying Content-Length can't sneak past the limit.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await file.Content.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > rules.MaxBytes) throw TooLarge(rules);
        }
        if (buffer.Length == 0)
            throw new DomainRuleException("file.empty", "The file is empty.");

        var bytes = buffer.ToArray();
        var type = Sniff(bytes);
        if (type is null || !rules.Allowed.Contains(type.Value))
            throw new DomainRuleException("file.type_not_allowed",
                $"Only {string.Join(", ", rules.Allowed.Select(t => t.ToString().ToUpperInvariant()))} files are accepted.");

        if (!await scanner.IsCleanAsync(bytes, ct))
            throw new DomainRuleException("file.infected", "The file failed the virus scan and was not saved.");

        var (contentType, extension) = type.Value switch
        {
            FileType.Pdf => ("application/pdf", ".pdf"),
            FileType.Jpeg => ("image/jpeg", ".jpg"),
            _ => ("image/png", ".png"),
        };
        return new ValidatedUpload(bytes, type.Value, contentType, extension, SafeName(file.FileName, extension),
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public static FileType? Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(PdfMagic)) return FileType.Pdf;
        if (bytes.StartsWith(PngMagic)) return FileType.Png;
        if (bytes.StartsWith(JpegMagic)) return FileType.Jpeg;
        return null;
    }

    /// <summary>Keeps a readable name for display and downloads, with the extension of the real type.</summary>
    public static string SafeName(string? original, string extension)
    {
        var name = Path.GetFileNameWithoutExtension(Path.GetFileName(original ?? string.Empty));
        name = UnsafeChars().Replace(name, "_").Trim('_', ' ', '.');
        if (name.Length == 0) name = "upload";
        if (name.Length > 80) name = name[..80];
        return name + extension;
    }

    private static DomainRuleException TooLarge(UploadRules rules) =>
        new("file.too_large", $"The file is larger than {rules.MaxBytes / UploadRules.MiB} MB.");

    [GeneratedRegex(@"[^A-Za-z0-9 _\-\.]")]
    private static partial Regex UnsafeChars();
}
