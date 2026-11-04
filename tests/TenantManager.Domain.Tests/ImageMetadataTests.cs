using System.Buffers.Binary;
using System.Text;
using Shouldly;
using TenantManager.Application.Files;

namespace TenantManager.Domain.Tests;

public class ImageMetadataTests
{
    /// <summary>
    /// A small JPEG with EXIF (Orientation = 6, a GPS latitude of 25° 44' S), an XMP block, a comment
    /// and some made-up scan data.
    /// </summary>
    private static (byte[] Jpeg, byte[] Scan) JpegWithLocation()
    {
        // TIFF, little endian: header (8) + IFD0 with 2 entries (2 + 24 + 4) = 38; GPS IFD at 38 with 2 entries (30) = 68.
        var tiff = new byte[68 + 24];
        "II"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 2);
        Entry(tiff, 10, 0x0112, 3, 1, 6);    // Orientation: rotate 90°
        Entry(tiff, 22, 0x8825, 4, 1, 38);   // GPS IFD pointer
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(38), 2);
        Entry(tiff, 40, 0x0001, 2, 2, 'S');  // GPSLatitudeRef "S"
        Entry(tiff, 52, 0x0002, 5, 3, 68);   // GPSLatitude: three rationals at offset 68
        uint[] latitude = [25, 1, 44, 1, 3000, 100];
        for (var i = 0; i < latitude.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(68 + i * 4), latitude[i]);

        var exif = Segment(0xE1, [.. "Exif\0\0"u8, .. tiff]);
        var xmp = Segment(0xE1, [.. "http://ns.adobe.com/xap/1.0/\0"u8, .. "<x:xmpmeta>Pretoria</x:xmpmeta>"u8]);
        var comment = Segment(0xFE, "Taken by Sipho"u8.ToArray());
        byte[] scan = [0xFF, 0xDA, 0x00, 0x04, 0x01, 0x02, 0x11, 0x22, 0x33, 0xFF, 0xD9];
        return ([0xFF, 0xD8, .. exif, .. xmp, .. comment, .. scan], scan);
    }

    private static void Entry(byte[] tiff, int at, ushort tag, ushort type, uint count, uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at + 2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at + 4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at + 8), value);
    }

    private static byte[] Segment(byte marker, byte[] payload)
    {
        var s = new byte[4 + payload.Length];
        s[0] = 0xFF;
        s[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(s.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(s, 4);
        return s;
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle);

    [Fact]
    public void The_GPS_location_is_removed_from_a_JPEG_but_the_orientation_is_kept()
    {
        var (jpeg, scan) = JpegWithLocation();

        var clean = ImageMetadata.Clean(jpeg, FileType.Jpeg);

        var tiff = IndexOf(clean, "Exif\0\0"u8) + 6;
        tiff.ShouldBeGreaterThan(5);
        var t = clean.AsSpan(tiff);
        BinaryPrimitives.ReadUInt16LittleEndian(t[10..]).ShouldBe((ushort)0x0112);
        BinaryPrimitives.ReadUInt32LittleEndian(t[18..]).ShouldBe(6u);               // orientation still 6
        BinaryPrimitives.ReadUInt16LittleEndian(t[38..]).ShouldBe((ushort)0);        // GPS IFD now empty
        t[40..92].ToArray().ShouldAllBe(b => b == 0);                                // entries and latitude zeroed
        clean.AsSpan(clean.Length - scan.Length).SequenceEqual(scan).ShouldBeTrue(); // image data untouched
    }

    [Fact]
    public void XMP_and_comments_are_dropped_from_a_JPEG()
    {
        var clean = ImageMetadata.Clean(JpegWithLocation().Jpeg, FileType.Jpeg);

        IndexOf(clean, "Pretoria"u8).ShouldBe(-1);
        IndexOf(clean, "Taken by Sipho"u8).ShouldBe(-1);
        clean[..2].ShouldBe(new byte[] { 0xFF, 0xD8 });
    }

    [Fact]
    public void Text_and_EXIF_chunks_are_dropped_from_a_PNG()
    {
        static byte[] Chunk(string type, byte[] data)
        {
            var c = new byte[12 + data.Length];
            BinaryPrimitives.WriteUInt32BigEndian(c, (uint)data.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(c, 4);
            data.CopyTo(c, 8);
            return c; // CRC left zero: not checked here
        }
        byte[] png =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            .. Chunk("IHDR", new byte[13]), .. Chunk("tEXt", "Location\0Midrand"u8.ToArray()),
            .. Chunk("eXIf", new byte[10]), .. Chunk("IDAT", [1, 2, 3]), .. Chunk("IEND", []),
        ];

        var clean = ImageMetadata.Clean(png, FileType.Png);

        IndexOf(clean, "Midrand"u8).ShouldBe(-1);
        IndexOf(clean, "eXIf"u8).ShouldBe(-1);
        IndexOf(clean, "IDAT"u8).ShouldBeGreaterThan(0);
        clean.Length.ShouldBe(png.Length - (12 + 16) - (12 + 10));
    }

    [Fact]
    public void A_file_that_does_not_parse_is_left_as_it_was()
    {
        byte[] broken = [0xFF, 0xD8, 0xFF, 0xE1, 0xFF, 0xFF, 0x01];
        ImageMetadata.Clean(broken, FileType.Jpeg).ShouldBe(broken);
        ImageMetadata.Clean("%PDF-1.4"u8.ToArray(), FileType.Pdf).ShouldBe("%PDF-1.4"u8.ToArray());
    }

    private sealed class CleanScanner : IFileScanner
    {
        public Task<bool> IsCleanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    [Fact]
    public async Task Uploaded_photos_are_stored_without_their_location_and_hashed_after_cleaning()
    {
        var (jpeg, _) = JpegWithLocation();
        var upload = await new UploadValidator(new CleanScanner())
            .ValidateAsync(new UploadedFile("tap.jpg", jpeg.Length, new MemoryStream(jpeg)), UploadRules.IssuePhoto, default);

        IndexOf(upload.Content, "Pretoria"u8).ShouldBe(-1);
        upload.Content.Length.ShouldBeLessThan(jpeg.Length);
        upload.Sha256.ShouldBe(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(upload.Content)));
    }
}
