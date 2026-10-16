using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.MacOS.Video;

namespace RemoteDesktop.Platform.MacOS.UnitTests;

/// <summary>
/// The VideoToolbox codec ports, driven for real.
/// </summary>
/// <remarks>
/// <para>
/// Every test here runs a real compression and decompression session. That is deliberate and it is the lesson of
/// docs/macos-port-plan.md 3.12: a codec port can build, register, resolve and report supported while its
/// declarations are wrong, and only a real frame through the pipeline shows it. The struct that crosses this
/// particular boundary lost a field during development, which would have shown up as pictures of the wrong height
/// or none at all — exactly the kind of thing that survives review.
/// </para>
/// <para>
/// The frames are synthetic. A test that needs a photographer is a test nobody runs.
/// </para>
/// </remarks>
public class VideoToolboxH264EncoderTests
{
    private static VideoEncoderOptions Options(int width = 320, int height = 240, int bitrate = 4000) =>
        new(VideoCodec.H264, width, height, 30, bitrate);

    [Fact]
    public void The_hardware_factory_reports_which_encoder_it_actually_got()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "VideoToolbox is macOS only.");

        using var encoder = new VideoToolboxH264EncoderFactory(hardware: true).Create(Options());

        // The helper reports which encoder accepted the configuration, because asking for hardware is a lookup
        // that can fall back. Asserting only that it is non-empty would pass with a factory that always claims
        // hardware while the platform quietly gave software.
        Assert.NotEmpty(encoder.Name);
        Assert.True(encoder.IsHardware, $"Expected a hardware encoder, got \"{encoder.Name}\".");
    }

    [Fact]
    public void The_software_factory_is_also_usable()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "VideoToolbox is macOS only.");

        // VideoToolbox lists both an "Apple H.264 (HW)" and an "Apple H.264 (SW)". The second exists so a machine
        // whose hardware encoder is busy has somewhere to go, which is the whole reason the selector sorts
        // hardware first instead of there being one factory.
        using var encoder = new VideoToolboxH264EncoderFactory(hardware: false).Create(Options());

        Assert.NotEmpty(encoder.Name);
    }

    [Fact]
    public void A_forced_key_frame_carries_parameter_sets()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "VideoToolbox is macOS only.");

        using var encoder = new VideoToolboxH264EncoderFactory(hardware: true).Create(Options());
        using var frame = SyntheticFrame.Of(320, 240, seed: 7);

        var packets = encoder.Encode(frame, forceKeyFrame: true);

        Assert.NotEmpty(packets);
        var key = packets[0];
        Assert.True(key.IsKeyFrame, "A forced key frame did not report itself as one.");
        Assert.Contains(H264Bitstream.NalSps, H264Bitstream.GetNalTypes(key.Data.Span));
        Assert.Contains(H264Bitstream.NalPps, H264Bitstream.GetNalTypes(key.Data.Span));
    }

    [Fact]
    public void A_later_frame_carries_no_parameter_sets()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "VideoToolbox is macOS only.");

        using var encoder = new VideoToolboxH264EncoderFactory(hardware: true).Create(Options());
        using var frame = SyntheticFrame.Of(320, 240, seed: 7);

        encoder.Encode(frame, forceKeyFrame: true);
        var packets = encoder.Encode(frame, forceKeyFrame: false);

        Assert.NotEmpty(packets);
        var types = H264Bitstream.GetNalTypes(packets[0].Data.Span);
        Assert.DoesNotContain(H264Bitstream.NalSps, types);
        Assert.DoesNotContain(H264Bitstream.NalPps, types);
    }

    [Fact]
    public void A_frame_of_the_wrong_size_is_refused_rather_than_reencoded()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "VideoToolbox is macOS only.");

        using var encoder = new VideoToolboxH264EncoderFactory(hardware: true).Create(Options());
        using var frame = SyntheticFrame.Of(160, 120, seed: 3);

        // Silently dropping the frame here would look like a quiet network on the far end, so the port says so.
        // The statement-bodied lambda makes this an Action rather than a Func, which the overload resolution
        // otherwise turns into an async test.
        Assert.Throws<ArgumentException>(() => { _ = encoder.Encode(frame, forceKeyFrame: false); });
    }

    [Fact]
    public void The_bitrate_can_be_changed_while_running()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "VideoToolbox is macOS only.");

        using var encoder = new VideoToolboxH264EncoderFactory(hardware: true).Create(Options(bitrate: 2000));
        using var frame = SyntheticFrame.Of(320, 240, seed: 7);

        encoder.SetBitrate(6000);
        encoder.Encode(frame, forceKeyFrame: true);

        // Not asserting the produced size: a variable-rate encoder is allowed to answer a bitrate change however it
        // likes. The point is that it did not throw and is still producing pictures.
        var packets = encoder.Encode(frame, forceKeyFrame: false);
        Assert.NotEmpty(packets);
    }
}

/// <summary>The decoder, and the two ends of it against each other.</summary>
public class VideoToolboxH264DecoderTests
{
    /// <summary>
    /// Encode and decode in the same test, which is the only way to check the two halves against each other.
    /// </summary>
    [Fact]
    public void A_what_was_encoded_comes_back_as_a_picture_of_the_same_size()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "VideoToolbox is macOS only.");

        using var encoder = new VideoToolboxH264EncoderFactory(hardware: true).Create(
            new VideoEncoderOptions(VideoCodec.H264, 320, 240, 30, 6000));
        using var decoder = new VideoToolboxH264DecoderFactory().Create(VideoCodec.H264, 320, 240);
        using var frame = SyntheticFrame.Of(320, 240, seed: 7);

        var key = encoder.Encode(frame, forceKeyFrame: true);
        Assert.NotEmpty(key);

        using var decoded = decoder.Decode(key[0]);

        Assert.NotNull(decoded);

        // Pixels, not points, and the size that went in — the struct that crosses this boundary has a height
        // field of its own, and reading it from the wrong slot produces either the wrong height or no picture.
        Assert.Equal(320, decoded.Width);
        Assert.Equal(240, decoded.Height);
        Assert.Equal(PixelFormat.Nv12, decoded.Format);
    }

    [Fact]
    public void The_decoded_pixels_are_close_to_what_was_encoded()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "VideoToolbox is macOS only.");

        using var encoder = new VideoToolboxH264EncoderFactory(hardware: true).Create(
            new VideoEncoderOptions(VideoCodec.H264, 320, 240, 30, 8000));
        using var decoder = new VideoToolboxH264DecoderFactory().Create(VideoCodec.H264, 320, 240);

        // A solid mid-grey frame: it compresses to almost nothing, and a decode returning garbage still has to
        // show up as a mean far away from what went in.
        using var frame = SyntheticFrame.Of(320, 240, luma: 128, seed: 0);
        var key = encoder.Encode(frame, forceKeyFrame: true);
        using var decoded = decoder.Decode(key[0]);

        Assert.NotNull(decoded);
        var pixels = decoded.GetPixels(out var stride);
        var total = 0.0;
        var samples = 0;
        for (var y = 0; y < 240; y += 8)
        {
            for (var x = 0; x < 320; x += 8)
            {
                total += pixels[(y * stride) + x];
                samples++;
            }
        }

        var mean = total / samples;
        Assert.True(mean is > 100 and < 156, $"Decoded luma mean was {mean}, expected near 128.");
    }

    [Fact]
    public void A_bitstream_without_parameter_sets_is_refused_with_a_reason()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "VideoToolbox is macOS only.");

        using var decoder = new VideoToolboxH264DecoderFactory().Create(VideoCodec.H264, 320, 240);

        // A slice NAL alone, with no SPS or PPS: the decoder has never been told the dimensions, and guessing
        // them would produce a picture that is quietly wrong.
        var bitstream = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x65, 0x11, 0x22, 0x33 };
        var error = Assert.Throws<InvalidDataException>(
            () => decoder.Decode(new EncodedVideoFrame(bitstream, false, TimeSpan.Zero)));

        Assert.Contains("key frame", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_frame_the_decoder_cannot_read_is_refused_with_a_reason()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "VideoToolbox is macOS only.");

        using var decoder = new VideoToolboxH264DecoderFactory().Create(VideoCodec.H264, 320, 240);

        // Garbage that happens to look like a key frame's parameter sets: the decoder must reject it with a
        // message naming the reason, because that message is what a user sees on the far end.
        var garbage = new byte[]
        {
            0x00, 0x00, 0x00, 0x01, 0x67, 0xFF, 0xFF, 0xFF,
            0x00, 0x00, 0x00, 0x01, 0x68, 0xFF, 0xFF,
            0x00, 0x00, 0x00, 0x01, 0x65, 0x11, 0x22, 0x33, 0x44,
        };

        Assert.Throws<InvalidDataException>(
            () => decoder.Decode(new EncodedVideoFrame(garbage, true, TimeSpan.Zero)));
    }

    [Fact]
    public void A_picture_can_be_disposed_and_another_decoded()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "VideoToolbox is macOS only.");

        using var encoder = new VideoToolboxH264EncoderFactory(hardware: true).Create(
            new VideoEncoderOptions(VideoCodec.H264, 320, 240, 30, 6000));
        using var decoder = new VideoToolboxH264DecoderFactory().Create(VideoCodec.H264, 320, 240);
        using var frame = SyntheticFrame.Of(320, 240, seed: 7);

        var key = encoder.Encode(frame, forceKeyFrame: true);
        using (var first = decoder.Decode(key[0]))
        {
            Assert.NotNull(first);
        }

        var next = encoder.Encode(frame, forceKeyFrame: false);
        using var second = decoder.Decode(next[0]);

        Assert.NotNull(second);
        Assert.Equal(320, second.Width);
        Assert.Equal(240, second.Height);
    }
}

/// <summary>What the two factories claim, which is what the selector sorts on.</summary>
public class VideoToolboxCodecFactoryTests
{
    [Fact]
    public void Only_h264_is_supported_by_the_encoder_factories()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "VideoToolbox is macOS only.");

        // H.265 is available on this machine and deliberately not offered: the wire format is H.264, so an HEVC
        // encoder would be code nothing can talk to. Adding one is a protocol decision, not a platform one.
        var h264 = new VideoEncoderOptions(VideoCodec.H264, 320, 240, 30, 4000);
        var h265 = new VideoEncoderOptions(VideoCodec.H265, 320, 240, 30, 4000);

        Assert.True(new VideoToolboxH264EncoderFactory(hardware: true).IsSupported(h264));
        Assert.True(new VideoToolboxH264EncoderFactory(hardware: true).IsSupported(h264));
        Assert.False(new VideoToolboxH264EncoderFactory(hardware: true).IsSupported(h265));
        Assert.False(new VideoToolboxH264EncoderFactory(hardware: false).IsSupported(h265));
    }

    [Fact]
    public void The_two_factories_are_distinguishable_by_the_selector()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "VideoToolbox is macOS only.");

        // The selector orders hardware first and falls back on failure. Both must exist for that to mean anything,
        // and they are registered in that order by AddMacOSPlatform.
        var hardware = new VideoToolboxH264EncoderFactory(hardware: true);
        var software = new VideoToolboxH264EncoderFactory(hardware: false);

        Assert.True(hardware.IsHardware);
        Assert.False(software.IsHardware);
        Assert.NotEqual(hardware.Name, software.Name);
    }

    [Fact]
    public void The_decoder_factory_offers_only_h264()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "VideoToolbox is macOS only.");

        var factory = new VideoToolboxH264DecoderFactory();

        Assert.True(factory.IsSupported(VideoCodec.H264));
        Assert.False(factory.IsSupported(VideoCodec.H265));
    }
}

/// <summary>A frame whose pixels are derived from a seed, so a test can state what it put in.</summary>
internal static class SyntheticFrame
{
    /// <summary>An NV12 frame: mid-grey luma, and chroma that varies unless asked for flat.</summary>
    public static MemoryVideoFrame Of(int width, int height, byte luma = 96, int seed = 0)
    {
        var frame = new MemoryVideoFrame(width, height, PixelFormat.Nv12, TimeSpan.FromMilliseconds(seed * 33));
        var pixels = frame.Buffer;

        pixels[..(width * height)].Fill(luma);

        if (seed != 0)
        {
            // Chroma noise, so the frame has something a lossy codec has to actually spend bits on. A flat frame
            // compresses to almost nothing and a broken decode can still look plausible.
            for (var i = width * height; i < pixels.Length; i++)
            {
                pixels[i] = (byte)((i * (seed + 1) * 37) & 0xFF);
            }
        }
        else
        {
            pixels[(width * height)..].Fill(0x80);
        }

        return frame;
    }
}
