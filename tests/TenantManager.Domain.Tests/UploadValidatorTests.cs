using System.Text;
using Shouldly;
using TenantManager.Application.Files;
using TenantManager.Domain.Common;

namespace TenantManager.Domain.Tests;

/// <summary>BR12: file type and size are checked on the server, from the file's own bytes.</summary>
public class UploadValidatorTests
{
    private sealed class FakeScanner(bool clean) : IFileScanner
    {
        public Task<bool> IsCleanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            Task.FromResult(clean);
    }

    private static readonly UploadValidator Validator = new(new FakeScanner(clean: true));

    public static byte[] Pdf(int size = 200)
    {
        var bytes = new byte[size];
        "%PDF-1.7\n"u8.CopyTo(bytes);
        return bytes;
    }

    public static byte[] Png(int size = 200)
    {
        var bytes = new byte[size];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    public static byte[] Jpeg(int size = 200)
    {
        var bytes = new byte[size];
        new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }.CopyTo(bytes, 0);
        return bytes;
    }

    private static UploadedFile File(string name, byte[] bytes) => new(name, bytes.Length, new MemoryStream(bytes));

    [Fact]
    public async Task BR12_Pdf_jpeg_and_png_are_accepted_and_typed_from_their_bytes()
    {
        (await Validator.ValidateAsync(File("eft.pdf", Pdf()), UploadRules.ProofOfPayment, default)).ContentType
            .ShouldBe("application/pdf");
        (await Validator.ValidateAsync(File("photo.PNG", Png()), UploadRules.ProofOfPayment, default)).ContentType
            .ShouldBe("image/png");
        (await Validator.ValidateAsync(File("scan.jpeg", Jpeg()), UploadRules.ProofOfPayment, default)).Extension
            .ShouldBe(".jpg");
    }

    [Fact]
    public async Task BR12_Exactly_5_MB_is_accepted_and_one_byte_more_is_rejected()
    {
        var limit = (int)(5 * UploadRules.MiB);
        await Should.NotThrowAsync(() => Validator.ValidateAsync(File("big.pdf", Pdf(limit)), UploadRules.ProofOfPayment, default));

        var ex = await Should.ThrowAsync<DomainRuleException>(() =>
            Validator.ValidateAsync(File("big.pdf", Pdf(limit + 1)), UploadRules.ProofOfPayment, default));
        ex.Code.ShouldBe("file.too_large");
    }

    [Fact]
    public async Task BR12_A_lying_size_header_does_not_get_past_the_limit()
    {
        var bytes = Pdf((int)(5 * UploadRules.MiB) + 10);
        var claimsSmall = new UploadedFile("big.pdf", Length: 100, new MemoryStream(bytes));

        (await Should.ThrowAsync<DomainRuleException>(() =>
            Validator.ValidateAsync(claimsSmall, UploadRules.ProofOfPayment, default))).Code.ShouldBe("file.too_large");
    }

    [Fact]
    public async Task BR12_A_renamed_executable_is_rejected()
    {
        var exe = Encoding.ASCII.GetBytes("MZ\u0090\0 this is a Windows program");
        (await Should.ThrowAsync<DomainRuleException>(() =>
            Validator.ValidateAsync(File("payment.pdf", exe), UploadRules.ProofOfPayment, default))).Code
            .ShouldBe("file.type_not_allowed");
    }

    [Fact]
    public async Task BR12_Issue_photos_must_be_images_not_pdfs()
    {
        (await Should.ThrowAsync<DomainRuleException>(() =>
            Validator.ValidateAsync(File("leak.pdf", Pdf()), UploadRules.IssuePhoto, default))).Code
            .ShouldBe("file.type_not_allowed");
    }

    [Fact]
    public async Task Empty_files_are_rejected()
    {
        (await Should.ThrowAsync<DomainRuleException>(() =>
            Validator.ValidateAsync(File("empty.pdf", []), UploadRules.ProofOfPayment, default))).Code.ShouldBe("file.empty");
    }

    [Fact]
    public async Task A_file_that_fails_the_virus_scan_is_rejected()
    {
        var infected = new UploadValidator(new FakeScanner(clean: false));
        (await Should.ThrowAsync<DomainRuleException>(() =>
            infected.ValidateAsync(File("eft.pdf", Pdf()), UploadRules.ProofOfPayment, default))).Code.ShouldBe("file.infected");
    }

    [Theory]
    [InlineData("../../etc/passwd.pdf", ".pdf", "passwd.pdf")]
    [InlineData("invoice.pdf.exe", ".pdf", "invoice.pdf.pdf")]
    [InlineData("Proof <script>.png", ".png", "Proof _script.png")]
    [InlineData("", ".jpg", "upload.jpg")]
    public void File_names_are_made_safe_and_get_the_real_extension(string original, string extension, string expected)
    {
        UploadValidator.SafeName(original, extension).ShouldBe(expected);
    }
}
