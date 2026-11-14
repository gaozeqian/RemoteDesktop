using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Media;

namespace RemoteDesktop.Core.UnitTests;

public class VideoCodecSelectorTests
{
    private static readonly VideoEncoderOptions Options = new(VideoCodec.H264, 1920, 1080, 60, 8000);

    [Fact]
    public void Hardware_encoder_is_preferred_over_higher_priority_software()
    {
        var software = new FakeEncoderFactory(isHardware: false, priority: 100);
        var hardware = new FakeEncoderFactory(isHardware: true, priority: 1);
        var selector = new VideoCodecSelector([software, hardware], []);

        using var encoder = selector.CreateEncoder(Options);

        Assert.Same(hardware, ((FakeEncoder)encoder).Factory);
    }

    [Fact]
    public void Falls_back_to_software_when_hardware_fails_to_initialize()
    {
        var hardware = new FakeEncoderFactory(isHardware: true, priority: 1, fails: true);
        var software = new FakeEncoderFactory(isHardware: false, priority: 1);
        var selector = new VideoCodecSelector([hardware, software], []);

        using var encoder = selector.CreateEncoder(Options);

        Assert.Same(software, ((FakeEncoder)encoder).Factory);
    }

    [Fact]
    public void Throws_when_nothing_supports_the_options()
    {
        var selector = new VideoCodecSelector([new FakeEncoderFactory(isHardware: true, priority: 1, supported: false)], []);

        Assert.Throws<NotSupportedException>(() => selector.CreateEncoder(Options));
    }

    [Fact]
    public void Hardware_is_skipped_when_the_caller_disallows_it()
    {
        var hardware = new FakeEncoderFactory(isHardware: true, priority: 100);
        var software = new FakeEncoderFactory(isHardware: false, priority: 1);
        var selector = new VideoCodecSelector([hardware, software], []);

        using var encoder = selector.CreateEncoder(Options, allowHardware: false);

        Assert.Same(software, ((FakeEncoder)encoder).Factory);
    }

    [Fact]
    public void Decoders_are_ordered_hardware_first_and_fall_back_to_software()
    {
        var software = new FakeDecoderFactory(isHardware: false, priority: 100);
        var hardware = new FakeDecoderFactory(isHardware: true, priority: 1, fails: true);
        var selector = new VideoCodecSelector([], [software, hardware]);

        Assert.Collection(
            selector.GetDecoderCandidates(VideoCodec.H264),
            candidate => Assert.Same(hardware, candidate),
            candidate => Assert.Same(software, candidate));

        using var decoder = selector.CreateDecoder(VideoCodec.H264, 1920, 1080);

        Assert.Same(software, ((FakeDecoder)decoder).Factory);
    }

    private sealed class FakeEncoderFactory(bool isHardware, int priority, bool fails = false, bool supported = true)
        : IVideoEncoderFactory
    {
        public string Name => isHardware ? "hardware" : "software";

        public bool IsHardware => isHardware;

        public int Priority => priority;

        public bool IsSupported(VideoEncoderOptions options) => supported;

        public IVideoEncoder Create(VideoEncoderOptions options) =>
            fails ? throw new InvalidOperationException("no device") : new FakeEncoder(this, options);
    }

    private sealed class FakeDecoderFactory(bool isHardware, int priority, bool fails = false, bool supported = true)
        : IVideoDecoderFactory
    {
        public string Name => isHardware ? "hardware" : "software";

        public bool IsHardware => isHardware;

        public int Priority => priority;

        public bool IsSupported(VideoCodec codec) => supported;

        public IVideoDecoder Create(VideoCodec codec, int width, int height) =>
            fails ? throw new InvalidOperationException("no device") : new FakeDecoder(this);
    }

    private sealed class FakeDecoder(FakeDecoderFactory factory) : IVideoDecoder
    {
        public FakeDecoderFactory Factory => factory;

        public string Name => factory.Name;

        public VideoFrame? Decode(EncodedVideoFrame frame) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FakeEncoder(FakeEncoderFactory factory, VideoEncoderOptions options) : IVideoEncoder
    {
        public FakeEncoderFactory Factory => factory;

        public string Name => factory.Name;

        public bool IsHardware => factory.IsHardware;

        public VideoEncoderOptions Options => options;

        public IReadOnlyList<EncodedVideoFrame> Encode(VideoFrame frame, bool forceKeyFrame) => [];

        public void SetBitrate(int bitrateKbps)
        {
        }

        public void Dispose()
        {
        }
    }
}
