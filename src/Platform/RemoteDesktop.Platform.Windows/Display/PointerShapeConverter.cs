namespace RemoteDesktop.Platform.Windows.Display;

/// <summary>
/// Turns the three pointer formats of Desktop Duplication (DXGI_OUTDUPL_POINTER_SHAPE_TYPE) into straight-alpha BGRA
/// that any renderer can draw on top of the video.
/// </summary>
internal static class PointerShapeConverter
{
    public const uint Monochrome = 1;
    public const uint Color = 2;
    public const uint MaskedColor = 4;

    /// <returns>Width, height and pixels of the pointer image, or <c>null</c> for a format or size that makes no sense.</returns>
    public static (int Width, int Height, byte[] Bgra)? Convert(uint type, int width, int height, int pitch, ReadOnlySpan<byte> data)
    {
        if (width is < 1 or > 512 || height is < 1 or > 1024 || pitch < 1)
        {
            return null;
        }

        return type switch
        {
            Color => ConvertColor(width, height, pitch, data),
            MaskedColor => ConvertMaskedColor(width, height, pitch, data),
            Monochrome => ConvertMonochrome(width, height, pitch, data),
            _ => null,
        };
    }

    private static (int, int, byte[])? ConvertColor(int width, int height, int pitch, ReadOnlySpan<byte> data)
    {
        if (data.Length < ((height - 1) * pitch) + (width * 4))
        {
            return null;
        }

        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            data.Slice(y * pitch, width * 4).CopyTo(bgra.AsSpan(y * width * 4, width * 4));
        }

        return (width, height, bgra);
    }

    // Alpha 0: the colour replaces the screen. Alpha 0xFF: the colour is XORed with the screen, which cannot be
    // reproduced on top of a video, so it is drawn opaque where it is not black.
    private static (int, int, byte[])? ConvertMaskedColor(int width, int height, int pitch, ReadOnlySpan<byte> data)
    {
        if (ConvertColor(width, height, pitch, data) is not var (_, _, bgra))
        {
            return null;
        }

        for (var i = 0; i < bgra.Length; i += 4)
        {
            var xor = bgra[i + 3] != 0;
            bgra[i + 3] = xor && bgra[i] == 0 && bgra[i + 1] == 0 && bgra[i + 2] == 0 ? (byte)0 : (byte)255;
        }

        return (width, height, bgra);
    }

    // The bitmap holds two stacked 1-bpp masks: AND on top, XOR below, so it is twice as tall as the pointer.
    //   AND 0, XOR 0: black     AND 0, XOR 1: white
    //   AND 1, XOR 0: unchanged (transparent)     AND 1, XOR 1: screen inverted, drawn as black
    private static (int, int, byte[])? ConvertMonochrome(int width, int height, int pitch, ReadOnlySpan<byte> data)
    {
        var pointerHeight = height / 2;
        if (pointerHeight < 1 || pitch < (width + 7) / 8 || data.Length < ((height - 1) * pitch) + ((width + 7) / 8))
        {
            return null;
        }

        var bgra = new byte[width * pointerHeight * 4];
        for (var y = 0; y < pointerHeight; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var mask = 0x80 >> (x % 8);
                var and = (data[(y * pitch) + (x / 8)] & mask) != 0;
                var xor = (data[((y + pointerHeight) * pitch) + (x / 8)] & mask) != 0;
                var p = ((y * width) + x) * 4;
                if (and && !xor)
                {
                    continue; // transparent
                }

                var value = !and && xor ? (byte)255 : (byte)0;
                bgra[p] = bgra[p + 1] = bgra[p + 2] = value;
                bgra[p + 3] = 255;
            }
        }

        return (width, pointerHeight, bgra);
    }
}
