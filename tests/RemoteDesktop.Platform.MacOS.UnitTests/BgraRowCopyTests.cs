using RemoteDesktop.Platform.Shared;

namespace RemoteDesktop.Platform.MacOS.UnitTests;

/// <summary>
/// The row copy both ScreenCaptureKit and AVFoundation sinks use. The bug this guards against is the one that made the
/// loop worth writing: a padded source stride read as if it were tight shifts every row after the first.
/// </summary>
public sealed class BgraRowCopyTests
{
    [Fact]
    public unsafe void Padded_source_rows_are_repacked_into_a_tight_destination()
    {
        const int width = 4;
        const int height = 3;
        const int destinationStride = width * 4;
        const int sourceStride = destinationStride + 8;

        var source = new byte[sourceStride * height];
        for (var i = 0; i < source.Length; i++)
        {
            source[i] = (byte)(i + 1);
        }

        var destination = new byte[destinationStride * height];
        fixed (byte* first = source)
        {
            BgraRowCopy.Copy(first, sourceStride, destination, destinationStride, height);
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < destinationStride; x++)
            {
                Assert.Equal(source[(y * sourceStride) + x], destination[(y * destinationStride) + x]);
            }
        }
    }

    [Fact]
    public unsafe void A_source_stride_narrower_than_the_destination_copies_only_its_own_bytes()
    {
        const int height = 2;
        const int sourceStride = 8;
        const int destinationStride = 16;
        var source = new byte[sourceStride * height];
        Array.Fill(source, (byte)0xAB);
        var destination = new byte[destinationStride * height];

        fixed (byte* first = source)
        {
            BgraRowCopy.Copy(first, sourceStride, destination, destinationStride, height);
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < sourceStride; x++)
            {
                Assert.Equal(0xAB, destination[(y * destinationStride) + x]);
            }

            for (var x = sourceStride; x < destinationStride; x++)
            {
                Assert.Equal(0, destination[(y * destinationStride) + x]);
            }
        }
    }
}
