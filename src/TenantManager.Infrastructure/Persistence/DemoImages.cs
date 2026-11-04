using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace TenantManager.Infrastructure.Persistence;

/// <summary>Tiny synthetic PNGs for the demo data (a colour block with diagonal stripes). No real photos are committed.</summary>
internal static class DemoImages
{
    public static byte[] Png(byte r, byte g, byte b, int width = 160, int height = 120)
    {
        var raw = new byte[height * (1 + width * 3)];
        var i = 0;
        for (var y = 0; y < height; y++)
        {
            raw[i++] = 0; // filter: none
            for (var x = 0; x < width; x++)
            {
                var stripe = (x + y) / 12 % 2 == 0;
                raw[i++] = stripe ? r : (byte)(r * 3 / 4);
                raw[i++] = stripe ? g : (byte)(g * 3 / 4);
                raw[i++] = stripe ? b : (byte)(b * 3 / 4);
            }
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 2; // colour type: RGB

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
        png.Write(four);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        png.Write(typeBytes);
        png.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(four, Crc32([.. typeBytes, .. data]));
        png.Write(four);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
