using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Linux.Video;

namespace RemoteDesktop.Platform.Linux.Tests;

/// <summary>
/// Decoding what this platform's encoder produces, which is the only pairing that actually ships.
/// </summary>
public sealed class VideoDecoderTests
{
    private const string LinuxOnly = "libavcodec, through the Linux platform layer.";

    private const int Width = 640;
    private const int Height = 480;

    private static VideoEncoderOptions Options => new(VideoCodec.H264, Width, Height, 30, 4000);

    private static MemoryVideoFrame Frame(byte luma)
    {
        var frame = new MemoryVideoFrame(Width, Height, PixelFormat.Nv12, TimeSpan.Zero);
        frame.Buffer[..(Width * Height)].Fill(luma);
        frame.Buffer[(Width * Height)..].Fill(0x80);
        return frame;
    }

    [Fact]
    public void A_key_frame_decodes_into_an_nv12_picture_of_the_agreed_size()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        using var encoder = new FfmpegH264Encoder("libx264", Options);
        using var source = Frame(0x80);
        var accessUnit = encoder.Encode(source, forceKeyFrame: true).Single();

        using var decoder = new FfmpegVideoDecoder("h264", Width, Height);
        using var decoded = decoder.Decode(accessUnit);

        Assert.NotNull(decoded);
        Assert.Equal(Width, decoded.Width);
        Assert.Equal(Height, decoded.Height);

        // The pipeline carries NV12 from here on, so a decoder that returned planar YUV would look almost right and
        // be wrong in the colour; this is what says which one came out.
        Assert.Equal(PixelFormat.Nv12, decoded.Format);
        Assert.Equal(MemoryVideoFrame.GetLength(PixelFormat.Nv12, Width, Height), decoded.GetPixels(out _).Length);
    }

    [Fact]
    public void A_bright_picture_decodes_bright_and_a_dark_one_dark()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        using var encoder = new FfmpegH264Encoder("libx264", Options);
        using var decoder = new FfmpegVideoDecoder("h264", Width, Height);

        var brightLuma = DecodeFlatLuma(encoder, decoder, 0xE0);
        var darkLuma = DecodeFlatLuma(encoder, decoder, 0x20);

        // Only the direction is asserted, not the value: H.264 is lossy, and a test that pinned the exact byte would
        // be testing the encoder's rate control rather than whether the picture survived the round trip.
        Assert.True(brightLuma > darkLuma, $"A bright frame ({brightLuma}) did not decode brighter than a dark one ({darkLuma}).");
    }

    [Fact]
    public void Frames_kept_by_the_caller_do_not_change_under_them()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        using var encoder = new FfmpegH264Encoder("libx264", Options);
        using var decoder = new FfmpegVideoDecoder("h264", Width, Height);
        using var dark = Frame(0x10);
        using var bright = Frame(0xE0);

        // A decoder reuses one picture internally. Handing that out would mean the frame a renderer is still
        // displaying changes the next time anything is decoded.
        using var first = decoder.Decode(encoder.Encode(dark, forceKeyFrame: true).Single());
        var firstLuma = Average(first!.GetPixels(out _)[..(Width * Height)]);

        using var second = decoder.Decode(encoder.Encode(bright, forceKeyFrame: false).Single());
        Assert.NotNull(second);

        var firstLumaAgain = Average(first.GetPixels(out _)[..(Width * Height)]);
        Assert.Equal(firstLuma, firstLumaAgain);
    }

    [Fact]
    public void A_decoder_that_has_lost_the_picture_can_start_again_from_a_key_frame()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        using var encoder = new FfmpegH264Encoder("libx264", Options);
        using var frame = Frame(0x80);
        var keyFrame = encoder.Encode(frame, forceKeyFrame: true).Single();

        // What happens after a packet loss: the controller throws the decoder away and starts a new session from the
        // next key frame, which has to work without anything that came before it.
        using (var first = new FfmpegVideoDecoder("h264", Width, Height))
        {
            Assert.NotNull(first.Decode(keyFrame));
        }

        using var second = new FfmpegVideoDecoder("h264", Width, Height);
        Assert.NotNull(second.Decode(keyFrame));
    }

    [Fact]
    public void A_bitstream_that_makes_no_sense_is_refused_rather_than_silently_dropped()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        using var decoder = new FfmpegVideoDecoder("h264", Width, Height);

        // Not a start code, not a NAL, not a picture. Reporting this lets the host ask for a key frame; returning
        // null would look the same as a decoder that simply needs more input, and the picture would never come back.
        var nonsense = new EncodedVideoFrame(new byte[256], IsKeyFrame: false, TimeSpan.Zero);
        Assert.Throws<InvalidDataException>(() => decoder.Decode(nonsense));
    }

    [Fact]
    public void A_decoder_that_is_not_in_this_build_is_refused_by_name()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var refused = Assert.Throws<NotSupportedException>(() => new FfmpegVideoDecoder("h264_from_the_future", Width, Height));
        Assert.Contains("h264_from_the_future", refused.Message, StringComparison.Ordinal);
    }

    private static byte DecodeFlatLuma(FfmpegH264Encoder encoder, FfmpegVideoDecoder decoder, byte luma)
    {
        using var source = Frame(luma);

        // The first unit of a stream carries the parameter sets; a picture decoded before those arrive can come back
        // as nothing at all, so the key frame is sent first and its output used.
        using var decoded = decoder.Decode(encoder.Encode(source, forceKeyFrame: true).Single());
        Assert.NotNull(decoded);

        return Average(decoded.GetPixels(out _)[..(Width * Height)]);
    }

    private static byte Average(ReadOnlySpan<byte> pixels)
    {
        long total = 0;
        foreach (var value in pixels)
        {
            total += value;
        }

        return (byte)(total / pixels.Length);
    }
}
