using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Features.Camera;
using RemoteDesktop.Media;
using RemoteDesktop.Protocol.Camera;
using RemoteDesktop.Protocol.Framing;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>A <see cref="CameraViewer"/> on a pipe; the fake decoder never has a picture unless told to.</summary>
public sealed class CameraViewerRig : ViewerRig
{
    private CameraViewerRig(Stream host, CameraViewer viewer, FakeDecoderFactory factory, SteppedTime time)
        : base(host, factory, time)
    {
        Viewer = viewer;
        viewer.Closed += (_, _) => Closed = true;
    }

    public CameraViewer Viewer { get; }

    public override long FrameNumber => Viewer.FrameNumber;

    public override int StreamWidth => Viewer.Details?.Width ?? 0;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<CameraViewerRig> StartAsync()
    {
        var (server, client) = await TestPipe.ConnectAsync(Ct);
        var factory = new FakeDecoderFactory();
        var time = new SteppedTime();
        var viewer = await StartViewerAsync(server, client, factory, time, width: 64, height: 64);
        var rig = new CameraViewerRig(client, viewer, factory, time);
        rig.StartPump();
        return rig;
    }

    /// <summary>Starts a viewer whose host answers with a stream of this size; the viewer owns <paramref name="server"/> from here on.</summary>
    public static async Task<CameraViewer> StartViewerAsync(Stream server, Stream client, FakeDecoderFactory factory, TimeProvider time, uint width, uint height)
    {
        var codecs = new VideoCodecSelector([], [factory]);
        var starting = CameraViewer.StartAsync(new FakeConnection(server), codecs, "cam", new CameraMode(64, 64, 30), time, NullLogger.Instance, Ct);
        _ = await ProtobufFrames.ReadAsync(client, CameraRequest.Parser, 1024, Ct);
        await ProtobufFrames.WriteAsync(client, StreamInfo(width, height), Ct);
        return await starting;
    }

    public override ValueTask SendStreamInfoAsync(uint width, uint height) => ProtobufFrames.WriteAsync(Host, StreamInfo(width, height), Ct);

    public override Task AskForKeyFrameAsync() => Viewer.RequestKeyFrameAsync(Ct);

    protected override ValueTask DisposeViewerAsync() => Viewer.DisposeAsync();

    protected override async ValueTask<bool?> ReadRequestAsync(Stream host, CancellationToken cancellationToken)
    {
        var message = await ProtobufFrames.ReadAsync(host, CameraRequest.Parser, 1024, cancellationToken);
        return message is null ? null : message.BodyCase == CameraRequest.BodyOneofCase.KeyFrameRequest;
    }

    private static CameraEvent StreamInfo(uint width, uint height) =>
        new() { StreamInfo = new CameraStreamInfo { Width = width, Height = height, FrameRate = 30, CameraName = "cam", EncoderName = "fake" } };
}
