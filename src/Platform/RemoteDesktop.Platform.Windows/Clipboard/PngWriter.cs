using System.Buffers.Binary;
using System.IO.Compression;

namespace RemoteDesktop.Platform.Windows.Clipboard;

/// <summary>Minimal PNG encoder for opaque 8-bit RGB pictures (what a clipboard screenshot needs), without a graphics library.</summary>
internal static class PngWriter
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = CreateCrcTable();

    /// <param name="rgb">Rows of <paramref name="width"/> × 3 bytes (R, G, B), top row first, no padding.</param>
    public static byte[] EncodeRgb(int width, int height, ReadOnlySpan<byte> rgb)
    {
        var rowBytes = width * 3;
        if (width < 1 || height < 1 || rgb.Length < rowBytes * height)
        {
            throw new ArgumentException("Pixel data does not match the picture size.");
        }

        using var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)height);
        header[8] = 8; // bit depth
        header[9] = 2; // colour type: RGB
        WriteChunk(output, "IHDR"u8, header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            Span<byte> filter = [0]; // filter type None for every row
            for (var y = 0; y < height; y++)
            {
                zlib.Write(filter);
                zlib.Write(rgb.Slice(y * rowBytes, rowBytes));
            }
        }

        WriteChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        output.Write(number);
        output.Write(type);
        output.Write(data);

        var crc = Crc(Crc(0xFFFFFFFF, type), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        output.Write(number);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
