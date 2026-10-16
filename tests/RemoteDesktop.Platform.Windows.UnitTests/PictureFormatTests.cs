using System.Buffers.Binary;
using System.IO.Compression;
using RemoteDesktop.Platform.Windows.Clipboard;
using RemoteDesktop.Platform.Windows.Display;

namespace RemoteDesktop.Platform.Windows.UnitTests;

/// <summary>The byte-level conversions behind clipboard pictures and the host's pointer image.</summary>
public class PictureFormatTests
{
    [Fact]
    public void The_png_encoder_writes_a_valid_file_that_decodes_back_to_the_same_pixels()
    {
        // 3 × 2: red, green, blue / white, black, grey.
        byte[] rgb = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 128, 128, 128];

        var png = PngWriter.EncodeRgb(3, 2, rgb);

        var decoded = DecodeRgb(png, out var width, out var height);
        Assert.Equal((3, 2), (width, height));
        Assert.Equal(rgb, decoded);
    }

    [Fact]
    public void A_bottom_up_32_bit_dib_becomes_a_top_down_opaque_png()
    {
        // 2 × 2 BGRA, bottom row first; the alpha byte is 0, as screenshots leave it.
        var dib = new byte[40 + (2 * 2 * 4)];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 32);
        byte[] pixels = [
            255, 0, 0, 0,   0, 255, 0, 0,      // bottom row: blue, green
            0, 0, 255, 0,   255, 255, 255, 0,  // top row: red, white
        ];
        pixels.CopyTo(dib.AsSpan(40));

        var png = DibConverter.ToPng(dib);

        Assert.NotNull(png);
        Assert.Equal<byte>([255, 0, 0, 255, 255, 255, 0, 0, 255, 0, 255, 0], DecodeRgb(png, out _, out _));
    }

    [Fact]
    public void A_24_bit_dib_pads_its_rows_to_four_bytes()
    {
        // 1 × 2 BGR: each row is 3 bytes of pixel + 1 byte of padding.
        var dib = new byte[40 + 8];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 24);
        new byte[] { 10, 20, 30, 0, 40, 50, 60, 0 }.CopyTo(dib.AsSpan(40)); // bottom row then top row

        var rgb = DecodeRgb(DibConverter.ToPng(dib)!, out _, out _);

        Assert.Equal<byte>([60, 50, 40, 30, 20, 10], rgb);
    }

    [Theory]
    [InlineData(8)]  // palette
    [InlineData(16)]
    public void Unsupported_bit_depths_are_skipped_instead_of_guessed(int bitCount)
    {
        var dib = new byte[40 + 64];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), (ushort)bitCount);

        Assert.Null(DibConverter.ToPng(dib));
    }

    [Fact]
    public void Truncated_or_absurd_dibs_are_rejected()
    {
        Assert.Null(DibConverter.ToPng(new byte[10]));

        var huge = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(huge, 40);
        BinaryPrimitives.WriteInt32LittleEndian(huge.AsSpan(4), 100_000);
        BinaryPrimitives.WriteInt32LittleEndian(huge.AsSpan(8), 100_000);
        BinaryPrimitives.WriteUInt16LittleEndian(huge.AsSpan(14), 32);
        Assert.Null(DibConverter.ToPng(huge));
    }

    [Fact]
    public void A_colour_pointer_is_copied_row_by_row_without_the_padding()
    {
        // 2 × 2 pointer in a buffer with 12 bytes per row (4 bytes of padding).
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 9, 9, 11, 12, 13, 14, 15, 16, 17, 18, 9, 9, 9, 9];

        var (width, height, bgra) = PointerShapeConverter.Convert(PointerShapeConverter.Color, 2, 2, 12, data)!.Value;

        Assert.Equal((2, 2), (width, height));
        Assert.Equal<byte>([1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 13, 14, 15, 16, 17, 18], bgra);
    }

    [Fact]
    public void A_monochrome_pointer_combines_its_and_and_xor_masks()
    {
        // 8 × 1 pointer: data holds 2 rows of 1 byte, AND mask then XOR mask.
        // Pixel 0: AND 0 XOR 0 → black.  1: AND 0 XOR 1 → white.  2: AND 1 XOR 0 → transparent.  3: AND 1 XOR 1 → inverted (black).
        byte[] data = [0b0011_0000, 0b0101_0000];

        var (width, height, bgra) = PointerShapeConverter.Convert(PointerShapeConverter.Monochrome, 8, 2, 1, data)!.Value;

        Assert.Equal((8, 1), (width, height));
        Assert.Equal<byte>([0, 0, 0, 255], bgra[0..4]);
        Assert.Equal<byte>([255, 255, 255, 255], bgra[4..8]);
        Assert.Equal(0, bgra[11]); // transparent
        Assert.Equal<byte>([0, 0, 0, 255], bgra[12..16]);
    }

    [Fact]
    public void A_masked_colour_pointer_turns_the_mask_into_alpha()
    {
        // Alpha 0 = plain colour → opaque. Alpha 255 = XOR: kept only where the colour is not black.
        byte[] data = [10, 20, 30, 0, 10, 20, 30, 255, 0, 0, 0, 255];

        var (_, _, bgra) = PointerShapeConverter.Convert(PointerShapeConverter.MaskedColor, 3, 1, 12, data)!.Value;

        Assert.Equal(255, bgra[3]);
        Assert.Equal(255, bgra[7]);
        Assert.Equal(0, bgra[11]);
    }

    [Theory]
    [InlineData(0u, 4, 4)]   // unknown type
    [InlineData(2u, 0, 4)]
    [InlineData(2u, 4, 100_000)]
    public void Nonsense_pointer_shapes_are_dropped(uint type, int width, int height) =>
        Assert.Null(PointerShapeConverter.Convert(type, width, height, 16, new byte[1024]));

    private static byte[] DecodeRgb(byte[] png, out int width, out int height)
    {
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);

        width = height = 0;
        using var idat = new MemoryStream();
        var position = 8;
        while (position < png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position));
            var type = System.Text.Encoding.ASCII.GetString(png, position + 4, 4);
            var data = png.AsSpan(position + 8, length);
            Assert.Equal(Crc32(png.AsSpan(position + 4, 4 + length)), BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position + 8 + length)));

            switch (type)
            {
                case "IHDR":
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                    Assert.Equal((8, 2), (data[8], data[9])); // 8-bit RGB
                    break;
                case "IDAT":
                    idat.Write(data);
                    break;
                default:
                    break;
            }

            position += 12 + length;
        }

        idat.Position = 0;
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
        {
            zlib.CopyTo(inflated);
        }

        // Every row starts with its filter type, which the encoder always sets to 0 (None).
        var raw = inflated.ToArray();
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            Assert.Equal(0, raw[y * ((width * 3) + 1)]);
            raw.AsSpan((y * ((width * 3) + 1)) + 1, width * 3).CopyTo(rgb.AsSpan(y * width * 3));
        }

        return rgb;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFF;
    }
}
