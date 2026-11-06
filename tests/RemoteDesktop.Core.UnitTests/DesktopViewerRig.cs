using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Media;
using RemoteDesktop.Protocol.Desktop;
using RemoteDesktop.Protocol.Framing;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>A <see cref="DesktopViewer"/> on a pipe; the fake decoder never has a picture unless told to.</summary>
public sealed class DesktopViewerRig : ViewerRig
{
    private DesktopViewerRig(Stream host, DesktopViewer viewer, FakeDecoderFactory factory, SteppedTime time)
        : base(host, factory, time)
    {
        Viewer = viewer;
        viewer.Closed += (_, _) => Closed = true;
    }

    public DesktopViewer Viewer { get; }

    public override long FrameNumber => Viewer.FrameNumber;

    public override int StreamWidth => Viewer.StreamInfo?.Width ?? 0;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<DesktopViewerRig> StartAsync(bool sendStreamInfo = true)
    {
        var (server, client) = await TestPipe.ConnectAsync(Ct);
        var factory = new FakeDecoderFactory();
        var time = new SteppedTime();
        var codecs = new VideoCodecSelector([], [factory]);
        var viewer = await DesktopViewer.StartAsync(new FakeConnection(server), codecs, 30, time, NullLogger.Instance, Ct);
        var rig = new DesktopViewerRig(client, viewer, factory, time);

        // The viewer's first request is read here; later ones are counted by the pump (the pipe has a buffer, an
        // unbuffered one would block the viewer's write until the host reads).
        _ = await ProtobufFrames.ReadAsync(client, VideoControl.Parser, 1024, Ct);
        rig.StartPump();
        if (sendStreamInfo)
        {
            await rig.SendStreamInfoAsync(64, 64);
            Assert.True(await WaitUntilAsync(() => factory.Created == 1), "the stream info creates the first decoder");
        }

        return rig;
    }

    public override ValueTask SendStreamInfoAsync(uint width, uint height) =>
        ProtobufFrames.WriteAsync(
            Host,
            new VideoControl { StreamInfo = new VideoStreamInfo { Width = width, Height = height, DisplayId = "d", EncoderName = "fake" } },
            Ct);

    public ValueTask SendControlAsync(VideoControl message) => ProtobufFrames.WriteAsync(Host, message, Ct);

    public override Task AskForKeyFrameAsync() => Viewer.RequestKeyFrameAsync(Ct);

    protected override ValueTask DisposeViewerAsync() => Viewer.DisposeAsync();

    protected override async ValueTask<bool?> ReadRequestAsync(Stream host, CancellationToken cancellationToken)
    {
        var message = await ProtobufFrames.ReadAsync(host, VideoControl.Parser, 1024, cancellationToken);
        return message is null ? null : message.BodyCase == VideoControl.BodyOneofCase.KeyFrameRequest;
    }
}
