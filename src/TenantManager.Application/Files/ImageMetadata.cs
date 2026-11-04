using System.Buffers.Binary;

namespace TenantManager.Application.Files;

/// <summary>
/// Removes location and other identifying metadata from uploaded photos (POPIA: don't keep personal
/// information we don't need). Phone photos usually carry GPS coordinates, which for a tenant means
/// their home address, plus the phone model and sometimes the owner's name.
/// <para>
/// JPEG: the GPS section of the EXIF data is emptied and zeroed, but the rest of EXIF is kept, because
/// browsers use its Orientation tag to show phone photos the right way up. XMP and IPTC blocks (which can
/// repeat the location and add names) and JPEG comments are dropped. PNG: text, time and EXIF chunks
/// are dropped. The image data itself is never touched, so there is no quality loss.
/// </para>
/// Anything that doesn't parse as expected is left as it was rather than risk corrupting the photo.
/// </summary>
public static class ImageMetadata
{
    private const ushort GpsIfdTag = 0x8825;

    public static byte[] Clean(byte[] bytes, FileType type) => type switch
    {
        FileType.Jpeg => CleanJpeg(bytes),
        FileType.Png => CleanPng(bytes),
        _ => bytes,
    };

    private static byte[] CleanJpeg(byte[] input)
    {
        if (input.Length < 4 || input[0] != 0xFF || input[1] != 0xD8) return input;
        using var output = new MemoryStream(input.Length);
        output.Write(input, 0, 2); // SOI
        var pos = 2;
        while (pos + 4 <= input.Length)
        {
            if (input[pos] != 0xFF) return input; // not a marker where one should be
            var marker = input[pos + 1];
            if (marker == 0xFF) { pos++; continue; } // fill byte
            if (marker == 0xDA || marker == 0xD9) break; // start of scan / end of image: copy the rest as is

            var length = BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(pos + 2));
            if (length < 2 || pos + 2 + length > input.Length) return input;
            var segment = input.AsSpan(pos, 2 + length);
            var payload = segment[4..];

            if (marker == 0xE1 && payload.StartsWith("Exif\0\0"u8))
            {
                var copy = segment.ToArray();
                RemoveGps(copy.AsSpan(4 + 6)); // the TIFF data after "Exif\0\0"
                output.Write(copy);
            }
            else if ((marker == 0xE1 && payload.StartsWith("http://ns.adobe.com/xap/1.0/"u8)) || // XMP
                     marker == 0xED || // APP13: Photoshop/IPTC (captions, names, places)
                     marker == 0xFE)   // comment
            {
                // dropped
            }
            else
            {
                output.Write(segment);
            }
            pos += 2 + length;
        }
        output.Write(input, pos, input.Length - pos);
        return output.ToArray();
    }

    /// <summary>Empties the GPS IFD in place: zero entries, its values zeroed, so no reader finds a location.</summary>
    private static void RemoveGps(Span<byte> tiff)
    {
        if (tiff.Length < 8) return;
        bool little;
        if (tiff[0] == 'I' && tiff[1] == 'I') little = true;
        else if (tiff[0] == 'M' && tiff[1] == 'M') little = false;
        else return;

        ushort U16(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
        uint U32(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);

        var ifd0 = U32(tiff[4..]);
        if (ifd0 + 2 > tiff.Length) return;
        var count0 = U16(tiff[(int)ifd0..]);
        for (var i = 0; i < count0; i++)
        {
            var entry = (int)ifd0 + 2 + i * 12;
            if (entry + 12 > tiff.Length) return;
            if (U16(tiff[entry..]) != GpsIfdTag) continue;

            var gps = U32(tiff[(entry + 8)..]);
            if (gps + 2 > tiff.Length) return;
            var gpsCount = U16(tiff[(int)gps..]);
            var entriesEnd = (int)gps + 2 + gpsCount * 12;
            if (entriesEnd + 4 > tiff.Length) return;

            for (var g = 0; g < gpsCount; g++)
            {
                var e = (int)gps + 2 + g * 12;
                var size = TypeSize(U16(tiff[(e + 2)..])) * (long)U32(tiff[(e + 4)..]);
                if (size > 4)
                {
                    var offset = U32(tiff[(e + 8)..]);
                    if (offset + size <= tiff.Length) tiff.Slice((int)offset, (int)size).Clear();
                }
            }
            tiff[((int)gps + 2)..(entriesEnd + 4)].Clear(); // the entries and the next-IFD pointer
            tiff.Slice((int)gps, 2).Clear();                 // entry count: 0
            return;
        }
    }

    private static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1, // byte, ascii, sbyte, undefined
        3 or 8 => 2,           // short
        4 or 9 or 11 => 4,     // long, float
        5 or 10 or 12 => 8,    // rational, double
        _ => 1,
    };

    private static readonly HashSet<string> DroppedPngChunks = ["eXIf", "tEXt", "iTXt", "zTXt", "tIME"];

    private static byte[] CleanPng(byte[] input)
    {
        const int signature = 8;
        if (input.Length < signature) return input;
        using var output = new MemoryStream(input.Length);
        output.Write(input, 0, signature);
        var pos = signature;
        while (pos + 12 <= input.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(pos));
            var total = 12L + length; // length, type, data, CRC
            if (pos + total > input.Length) return input;
            var type = System.Text.Encoding.ASCII.GetString(input, pos + 4, 4);
            if (!DroppedPngChunks.Contains(type)) output.Write(input, pos, (int)total);
            pos += (int)total;
            if (type == "IEND") return output.ToArray();
        }
        return input; // no IEND: not a well-formed PNG, so leave it alone
    }
}
