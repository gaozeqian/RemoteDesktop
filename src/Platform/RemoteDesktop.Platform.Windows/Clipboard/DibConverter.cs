using System.Buffers.Binary;

namespace RemoteDesktop.Platform.Windows.Clipboard;

/// <summary>Converts the device-independent bitmap of <c>CF_DIB</c> (what screenshots put on the clipboard) to PNG.</summary>
internal static class DibConverter
{
    private const int MaxPixels = 32 * 1024 * 1024;

    private const int BiRgb = 0;
    private const int BiBitfields = 3;

    /// <returns>PNG bytes, or <c>null</c> for a bitmap format that is not supported (palettes, compression) or implausible.</returns>
    public static byte[]? ToPng(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 40)
        {
            return null;
        }

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        var width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        var compression = BinaryPrimitives.ReadInt32LittleEndian(dib[16..]);

        var topDown = signedHeight < 0;
        var height = Math.Abs(signedHeight);
        if (headerSize < 40 || width < 1 || height < 1 || (long)width * height > MaxPixels || bitCount is not (24 or 32) ||
            compression is not (BiRgb or BiBitfields))
        {
            return null;
        }

        // With BI_BITFIELDS the three channel masks follow a plain 40-byte header; larger headers contain them.
        var pixelOffset = headerSize + (compression == BiBitfields && headerSize == 40 ? 12 : 0);
        var stride = (((width * bitCount) + 31) / 32) * 4;
        if (dib.Length < pixelOffset + ((long)stride * height))
        {
            return null;
        }

        // Alpha of a clipboard bitmap is unreliable (screenshots leave it at 0), so the picture is treated as opaque.
        var bytesPerPixel = bitCount / 8;
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            var source = dib[(pixelOffset + ((topDown ? y : height - 1 - y) * stride))..];
            var target = rgb.AsSpan(y * width * 3, width * 3);
            for (var x = 0; x < width; x++)
            {
                target[x * 3] = source[(x * bytesPerPixel) + 2];
                target[(x * 3) + 1] = source[(x * bytesPerPixel) + 1];
                target[(x * 3) + 2] = source[x * bytesPerPixel];
            }
        }

        return PngWriter.EncodeRgb(width, height, rgb);
    }
}
