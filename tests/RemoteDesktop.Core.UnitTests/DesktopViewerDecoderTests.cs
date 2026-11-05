using System.IO.Pipes;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Media;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Desktop;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The controller's answer to a decoder that keeps being fed and stops producing pictures. The host is played by the
/// other end of a pipe and the decoder by a fake that never has a picture; what is asserted is when the viewer gives
/// up on that decoder and builds another.
/// </summary>
public sealed class DesktopViewerDecoderTests
{
    // The viewer's own stall timeout, which counts from the stream info. These tests wait it out in real time because
    // the viewer measures it with the monotonic clock.
    private static readonly TimeSpan PastStallTimeout = TimeSpan.FromMilliseconds(2300);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_run_of_silent_non_key_frames_rebuilds_the_decoder()
    {
        await using var rig = await Rig.StartAsync();
        await rig.SendKeyFrameAsync();
        await rig.WaitForDecodesAsync(1);
        await Task.Delay(PastStallTimeout, Ct);

        await rig.SendDeltaFramesAsync(2);
        await rig.WaitForDecodesAsync(3);
        Assert.Equal(1, rig.Factory.Created);

        await rig.SendDeltaFramesAsync(1);
        Assert.True(await WaitUntilAsync(() => rig.Factory.Created == 2), "the third silent frame in a row rebuilds the decoder");
        Assert.True(rig.Factory.Decoders[0].Disposed, "the decoder that lost sync is released");
    }

    [Fact]
    public async Task A_key_frame_the_decoder_has_nothing_for_does_not_count_towards_a_rebuild()
    {
        await using var rig = await Rig.StartAsync();
        await Task.Delay(PastStallTimeout, Ct);

        // Parameter sets on their own give no picture; throwing the decoder away over them would lose what it learned.
        for (var i = 0; i < 6; i++)
        {
            await rig.SendKeyFrameAsync();
        }

        await rig.WaitForDecodesAsync(6);
        await Task.Delay(200, Ct);
        Assert.Equal(1, rig.Factory.Created);
    }

    [Fact]
    public async Task A_decoder_that_cannot_be_rebuilt_leaves_the_old_one_in_place_and_the_stream_open()
    {
        await using var rig = await Rig.StartAsync();
        await rig.SendKeyFrameAsync();
        await rig.WaitForDecodesAsync(1);
        await Task.Delay(PastStallTimeout, Ct);

        rig.Factory.FailNextCreate = true;
        await rig.SendDeltaFramesAsync(3);
        await rig.WaitForDecodesAsync(4);
        Assert.True(await WaitUntilAsync(() => rig.Factory.FailedCreates == 1), "a rebuild was attempted");

        // The viewer asked for a key frame and is waiting for it; the old decoder must still be there to take it.
        await rig.SendKeyFrameAsync();
        await rig.WaitForDecodesAsync(5);
        Assert.False(rig.Factory.Decoders[0].Disposed);
        Assert.False(rig.Closed);
    }

    [Fact]
    public async Task A_malformed_video_frame_is_dropped_and_the_stream_goes_on()
    {
        await using var rig = await Rig.StartAsync();
        rig.Factory.ProducePictures = true;

        // Shorter than the video frame header: Parse refuses it. That costs one frame, not the whole viewer.
        await rig.SendRawVideoFrameAsync(new byte[3]);
        await rig.SendKeyFrameAsync();

        Assert.True(await WaitUntilAsync(() => rig.Viewer.FrameNumber >= 1), "a picture still arrives after the bad frame");
        Assert.False(rig.Closed);
    }

    [Fact]
    public async Task A_stream_description_that_cannot_be_used_still_ends_the_viewer()
    {
        await using var rig = await Rig.StartAsync();

        await rig.SendStreamInfoAsync(width: 0, height: 0);

        Assert.True(await WaitUntilAsync(() => rig.Closed), "an unusable stream description cannot be recovered from");
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
        }

        return condition();
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly Stream _host;

        private Rig(Stream host, DesktopViewer viewer, FakeDecoderFactory factory)
        {
            _host = host;
            Viewer = viewer;
            Factory = factory;
            viewer.Closed += (_, _) => Closed = true;
        }

        public DesktopViewer Viewer { get; }

        public FakeDecoderFactory Factory { get; }

        public bool Closed { get; private set; }

        public static async Task<Rig> StartAsync()
        {
            var name = $"rd-unit-{Guid.NewGuid():N}";
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(server.WaitForConnectionAsync(Ct), client.ConnectAsync(Ct));

            var factory = new FakeDecoderFactory();
            var codecs = new VideoCodecSelector([], [factory]);
            var viewer = await DesktopViewer.StartAsync(new FakeConnection(server), codecs, 30, TimeProvider.System, NullLogger.Instance, Ct);
            var rig = new Rig(client, viewer, factory);

            // Nothing is read on the host side except the viewer's first request; later key frame requests simply sit
            // in the pipe, which has a buffer (an unbuffered one would block the viewer's write until the host reads).
            _ = await ProtobufFrames.ReadAsync(client, VideoControl.Parser, 1024, Ct);
            await ProtobufFrames.WriteAsync(
                client,
                new VideoControl { StreamInfo = new VideoStreamInfo { Width = 64, Height = 64, DisplayId = "d", EncoderName = "fake" } },
                Ct);
            Assert.True(await WaitUntilAsync(() => factory.Created == 1), "the stream info creates the first decoder");
            return rig;
        }

        public ValueTask SendKeyFrameAsync() => SendAsync(isKeyFrame: true);

        public ValueTask SendRawVideoFrameAsync(byte[] payload) =>
            FrameCodec.WriteAsync(_host, new Frame(VideoDataFrame.MessageType, payload), Ct);

        public ValueTask SendStreamInfoAsync(uint width, uint height) =>
            ProtobufFrames.WriteAsync(
                _host,
                new VideoControl { StreamInfo = new VideoStreamInfo { Width = width, Height = height, DisplayId = "d", EncoderName = "fake" } },
                Ct);

        public async ValueTask SendDeltaFramesAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                await SendAsync(isKeyFrame: false);
            }
        }

        public async Task WaitForDecodesAsync(int count) =>
            Assert.True(await WaitUntilAsync(() => Factory.Decodes >= count), $"{count} frame(s) reached a decoder");

        private ValueTask SendAsync(bool isKeyFrame) =>
            VideoDataFrame.WriteAsync(_host, new EncodedVideoFrame(new byte[] { 0, 0, 0, 1, 0x65 }, isKeyFrame, TimeSpan.Zero), DateTimeOffset.UtcNow, Ct);

        public async ValueTask DisposeAsync()
        {
            await Viewer.DisposeAsync();
            await _host.DisposeAsync();
        }
    }

    private sealed class FakeDecoderFactory : IVideoDecoderFactory
    {
        private int _created;
        private int _failedCreates;
        private int _decodes;

        public string Name => "fake";

        public bool IsHardware => false;

        public int Priority => 0;

        public bool FailNextCreate { get; set; }

        /// <summary>When set, decoders answer every unit with a picture instead of nothing.</summary>
        public bool ProducePictures { get; set; }

        public List<FakeDecoder> Decoders { get; } = [];

        public int Created => Volatile.Read(ref _created);

        public int FailedCreates => Volatile.Read(ref _failedCreates);

        public int Decodes => Volatile.Read(ref _decodes);

        public bool IsSupported(VideoCodec codec) => true;

        public IVideoDecoder Create(VideoCodec codec, int width, int height)
        {
            if (FailNextCreate)
            {
                FailNextCreate = false;
                Interlocked.Increment(ref _failedCreates);
                throw new InvalidOperationException("No decoder left.");
            }

            var decoder = new FakeDecoder(() => Interlocked.Increment(ref _decodes), () => ProducePictures);
            lock (Decoders)
            {
                Decoders.Add(decoder);
            }

            Interlocked.Increment(ref _created);
            return decoder;
        }
    }

    /// <summary>By default a decoder that has lost sync for good: it takes everything and shows nothing.</summary>
    private sealed class FakeDecoder(Action onDecode, Func<bool> producePicture) : IVideoDecoder
    {
        public string Name => "fake";

        public bool Disposed { get; private set; }

        public VideoFrame? Decode(EncodedVideoFrame frame)
        {
            onDecode();
            return producePicture() ? new MemoryVideoFrame(64, 64, PixelFormat.Nv12, TimeSpan.Zero) : null;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeConnection(Stream stream) : IPeerConnection
    {
        public DeviceId RemoteDevice => new(111_111_111);

        public ConnectionPath Path => ConnectionPath.Direct;

        public string? RelaySessionId => null;

        public string LocalFingerprint => "local";

        public string RemoteFingerprint => "remote";

        public ValueTask<IChannel> OpenChannelAsync(ChannelType type, bool bidirectional, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IChannel>(new FakeChannel(type, stream));

        public ValueTask<IChannel?> AcceptChannelAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IChannel?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeChannel(ChannelType type, Stream stream) : IChannel
    {
        public ChannelType Type => type;

        public Stream Stream => stream;

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
