using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Linux.Video;

namespace RemoteDesktop.Platform.Linux.Tests;

/// <summary>
/// The software H.264 encoder, against the real libavcodec on this machine.
/// </summary>
/// <remarks>
/// What is asserted here is the contract the host depends on rather than the picture: Annex-B framing, a key frame
/// when one is asked for and not otherwise, no B-frames, and a bitrate that can be moved without rebuilding the
/// encoder. A decoder round trip lives in <see cref="VideoDecoderTests"/>.
/// </remarks>
public sealed class VideoEncoderTests
{
    private const string LinuxOnly = "libavcodec, through the Linux platform layer.";

    private static VideoEncoderOptions Options(int bitrateKbps = 4000, EncoderPreference preference = EncoderPreference.Smoothness) =>
        new(VideoCodec.H264, 640, 480, 30, bitrateKbps, preference);

    /// <summary>An NV12 frame of one flat colour, which is what a screen with nothing happening looks like.</summary>
    private static MemoryVideoFrame Frame(byte y = 0x40, byte uv = 0x80)
    {
        var frame = new MemoryVideoFrame(640, 480, PixelFormat.Nv12, TimeSpan.Zero);
        frame.Buffer[..(640 * 480)].Fill(y);
        frame.Buffer[(640 * 480)..].Fill(uv);
        return frame;
    }

    [Fact]
    public void The_encoder_encodes_and_says_what_it_is()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        using var encoder = new FfmpegH264Encoder("libx264", Options());

        Assert.False(encoder.IsHardware);
        Assert.Contains("x264", encoder.Name, StringComparison.OrdinalIgnoreCase);

        using var frame = Frame();
        var produced = Feed(encoder, frame, frames: 30);

        Assert.NotEmpty(produced);
    }

    [Fact]
    public void The_output_is_an_annex_b_stream()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        using var encoder = new FfmpegH264Encoder("libx264", Options());
        using var frame = Frame();

        var produced = Feed(encoder, frame, frames: 10);
        var first = produced[0].Data;

        // The wire format and H264Bitstream both parse start codes; a length-prefixed (AVCC) stream would be
        // silently unreadable to both, which is why this is asserted rather than assumed.
        Assert.Contains(H264Bitstream.GetNalTypes(first.Span), nal => nal is 7 or 5);
        Assert.True(HasStartCode(first.Span), "The first access unit has no Annex-B start code.");
    }

    [Fact]
    public void A_key_frame_carries_its_parameter_sets_in_band()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        using var encoder = new FfmpegH264Encoder("libx264", Options());
        using var frame = Frame();

        var produced = Feed(encoder, frame, frames: 5);

        // A controller that joins mid-stream is sent a key frame and nothing before it, so the sequence and picture
        // parameter sets have to be inside that access unit — they cannot arrive some other way.
        Assert.True(H264Bitstream.IsKeyFrame(produced[0].Data.Span));
        Assert.True(H264Bitstream.ContainsParameterSets(produced[0].Data.Span));
    }

    [Fact]
    public void A_key_frame_is_produced_only_when_one_was_asked_for()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        using var encoder = new FfmpegH264Encoder("libx264", Options());
        using var frame = Frame();

        // The first frame of a stream is a key frame, because there is nothing before it to refer to.
        Assert.True(H264Bitstream.IsKeyFrame(encoder.Encode(frame, forceKeyFrame: false)[0].Data.Span));

        // After that, nothing until asked. The GOP is ten seconds long precisely so that nothing else produces one:
        // a key frame is large, and the controller asks for one when it has actually lost the picture.
        for (var i = 0; i < 60; i++)
        {
            foreach (var encoded in encoder.Encode(frame, forceKeyFrame: false))
            {
                Assert.False(H264Bitstream.IsKeyFrame(encoded.Data.Span), $"Frame {i} was a key frame nobody asked for.");
            }
        }

        var asked = encoder.Encode(frame, forceKeyFrame: true).Where(e => H264Bitstream.IsKeyFrame(e.Data.Span));
        Assert.NotEmpty(asked);
    }

    [Fact]
    public void No_b_frames_are_produced()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        using var encoder = new FfmpegH264Encoder("libx264", Options());
        using var frame = Frame();

        // B-frames reorder output, which means holding frames back. For a remote desktop that is latency spent on
        // nothing, so the encoder is configured for none. Read back from the encoder rather than remembered, because
        // a setting the encoder quietly ignored would look the same from here — and the picture would be a few
        // frames behind on a link that looks perfectly healthy.
        Assert.Equal(0, encoder.MaxBFrames);

        Assert.NotEmpty(Feed(encoder, frame, frames: 60));
        Assert.Equal(0, encoder.MaxBFrames);
    }

    [Fact]
    public void The_bitrate_can_be_moved_without_rebuilding_the_encoder()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        using var encoder = new FfmpegH264Encoder("libx264", Options(bitrateKbps: 8000));
        using var frame = Frame();

        Feed(encoder, frame, frames: 10);

        // The host adjusts this as often as every half second on a congested link. An implementation that only took
        // a bitrate at construction would turn a slow link into a stalled one.
        encoder.SetBitrate(600);
        var afterLowering = Feed(encoder, frame, frames: 30);

        encoder.SetBitrate(12000);
        var afterRaising = Feed(encoder, frame, frames: 30);

        Assert.NotEmpty(afterLowering);
        Assert.NotEmpty(afterRaising);
    }

    [Fact]
    public void A_frame_of_the_wrong_shape_is_refused_with_a_reason()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        using var encoder = new FfmpegH264Encoder("libx264", Options());
        using var right = Frame();
        using var wrong = new MemoryVideoFrame(320, 240, PixelFormat.Nv12, TimeSpan.Zero);

        var refused = Assert.Throws<InvalidDataException>(() => encoder.Encode(wrong, forceKeyFrame: false));
        Assert.Contains("320x240", refused.Message, StringComparison.Ordinal);

        // And an encoder that refused one frame still works on the next, rather than being left half-consumed.
        Assert.NotEmpty(encoder.Encode(right, forceKeyFrame: true));
    }

    [Fact]
    public void An_encoder_that_is_not_in_this_build_is_refused_by_name()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var refused = Assert.Throws<NotSupportedException>(() => new FfmpegH264Encoder("h264_from_the_future", Options()));
        Assert.Contains("h264_from_the_future", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var encoder = new FfmpegH264Encoder("libx264", Options());
        encoder.Dispose();
        encoder.Dispose();
    }

    private static List<EncodedVideoFrame> Feed(FfmpegH264Encoder encoder, MemoryVideoFrame frame, int frames)
    {
        var produced = new List<EncodedVideoFrame>();
        for (var i = 0; i < frames; i++)
        {
            produced.AddRange(encoder.Encode(frame, forceKeyFrame: false));
        }

        return produced;
    }

    private static bool HasStartCode(ReadOnlySpan<byte> data) =>
        data.Length >= 4
        && ((data[0] == 0 && data[1] == 0 && data[2] == 1)
            || (data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1));
}
