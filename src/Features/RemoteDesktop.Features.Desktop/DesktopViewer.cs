using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Features.Shared;
using RemoteDesktop.Media;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Desktop;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Features.Desktop;

public sealed record DesktopStreamInfo(string DisplayId, int Width, int Height, string EncoderName, bool HardwareEncoder, string DecoderName);

public sealed record DesktopStatistics(double FramesPerSecond, double Kbps, double DecodeMilliseconds, double? LatencyMilliseconds);

/// <summary>
/// Controller side of the video channel: requests a stream, decodes it and keeps the newest picture as BGRA for the
/// UI to copy. Decoding runs on a background loop; <see cref="FrameReady"/> is raised from that loop.
/// </summary>
public sealed partial class DesktopViewer : IAsyncDisposable
{
    private readonly IChannel _channel;
    private readonly VideoDecoderSupervisor _supervisor;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly VideoFrameBuffer _frames = new();
    private readonly VideoStatisticsMeter _meter = new();
    private readonly VideoReceiveLoop _receive;
    private Task _loop = Task.CompletedTask;
    private Task _watchdog = Task.CompletedTask;
    private volatile bool _stalled;

    private DesktopViewer(IChannel channel, VideoCodecSelector codecs, TimeProvider time, ILogger logger)
    {
        _channel = channel;
        _time = time;
        _logger = logger;

        // The same clock as the watchdog tick, so a test can step silence forward instead of waiting two seconds.
        _supervisor = new VideoDecoderSupervisor(codecs, time, ex => LogDecoderRebuildFailed(_logger, ex));
        _receive = new VideoReceiveLoop(
            channel.Stream,
            _supervisor,
            _frames,
            _meter,
            time,
            HandleEnvelope,
            RequestKeyFrameAsync,
            pictureShown: () => _stalled = false,
            statisticsUpdated: snapshot => Statistics = new DesktopStatistics(
                snapshot.FramesPerSecond, snapshot.Kbps, snapshot.DecodeMilliseconds, snapshot.LatencyMilliseconds),
            frameReady: () => FrameReady?.Invoke(this, EventArgs.Empty),
            _logger);
    }

    public DesktopStreamInfo? StreamInfo { get; private set; }

    public DesktopStatistics Statistics { get; private set; } = new(0, 0, 0, null);

    /// <summary>
    /// Whether the host has stopped answering key-frame requests, so the picture on screen is older than it claims
    /// to be. A static screen is not a stall: the host answers a request even when nothing changed.
    /// </summary>
    public bool IsStalled => _stalled;

    /// <summary>Total number of frames decoded so far.</summary>
    public long FrameNumber => _frames.FrameNumber;

    /// <summary>Displays the host offered, for switching (M4).</summary>
    public IReadOnlyList<DisplayDescription> Displays { get; private set; } = [];

    /// <summary>Raised when the stream starts or changes resolution.</summary>
    public event EventHandler? StreamInfoChanged;

    /// <summary>Raised from the receive loop when the host's pointer image changes.</summary>
    public event EventHandler<CursorShape>? CursorShapeChanged;

    /// <summary>Raised from the receive loop when the host's pointer moves; positions are in pixels of the stream.</summary>
    public event EventHandler<CursorPosition>? CursorMoved;

    /// <summary>Raised from the decode loop after a new picture is available via <see cref="CopyLatestFrame"/>.</summary>
    public event EventHandler? FrameReady;

    /// <summary>Raised once when the stream ends (host stopped, connection lost, disposed).</summary>
    public event EventHandler? Closed;

    public static async Task<DesktopViewer> StartAsync(
        IPeerConnection connection,
        VideoCodecSelector codecs,
        int maxFrameRate,
        TimeProvider time,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var channel = await connection.OpenChannelAsync(ChannelType.Video, bidirectional: true, cancellationToken).ConfigureAwait(false);
        var viewer = new DesktopViewer(channel, codecs, time, logger);
        try
        {
            await viewer.WriteAsync(new VideoControl { Request = new VideoRequest { MaxFrameRate = (uint)maxFrameRate } }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await viewer.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        viewer._loop = Task.Run(viewer.ReceiveLoopAsync, CancellationToken.None);
        viewer._watchdog = viewer.KeyFrameWatchdogAsync();
        return viewer;
    }

    /// <summary>
    /// Copies the newest picture into <paramref name="destination"/> (BGRA, <paramref name="stride"/> bytes per row).
    /// Returns its sequence number, or <c>-1</c> when there is no picture of the current size yet.
    /// </summary>
    public long CopyLatestFrame(Span<byte> destination, int stride, int width, int height) =>
        _frames.CopyLatest(destination, stride, width, height);

    /// <summary>Asks the host to stream another of its displays; it answers with a new stream info.</summary>
    public Task SelectDisplayAsync(string displayId, CancellationToken cancellationToken) =>
        WriteAsync(new VideoControl { SelectDisplay = new SelectDisplay { DisplayId = displayId } }, cancellationToken).AsTask();

    public Task RequestKeyFrameAsync(CancellationToken cancellationToken) =>
        WriteAsync(new VideoControl { KeyFrameRequest = new KeyFrameRequest() }, cancellationToken).AsTask();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _channel.DisposeAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        await _watchdog.ConfigureAwait(false);
        _supervisor.Dispose();
        _stop.Dispose();
        _writeLock.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            await _receive.RunAsync(_stop.Token).ConfigureAwait(false);
        }
        finally
        {
#pragma warning disable CA1031 // A subscriber's failure must not fault the receive loop (and resurface from Dispose).
            try
            {
                Closed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                LogCloseHandlerFailed(_logger, ex);
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>Hands one control envelope to the viewer; false means the frame is not one.</summary>
    private bool HandleEnvelope(Frame frame)
    {
        if (frame.MessageType != ProtobufFrames.EnvelopeMessageType)
        {
            return false;
        }

        var control = VideoControl.Parser.ParseFrom(frame.Payload.Span);
        switch (control.BodyCase)
        {
            case VideoControl.BodyOneofCase.StreamInfo:
                OnStreamInfo(control.StreamInfo);
                break;
            case VideoControl.BodyOneofCase.CursorShape when IsValidCursor(control.CursorShape):
                var shape = control.CursorShape;
                CursorShapeChanged?.Invoke(this, new CursorShape((int)shape.Width, (int)shape.Height, shape.HotspotX, shape.HotspotY, shape.Bgra.ToByteArray()));
                break;
            case VideoControl.BodyOneofCase.CursorPosition:
                var position = control.CursorPosition;
                CursorMoved?.Invoke(this, new CursorPosition(position.X, position.Y, position.Visible));
                break;
            default:
                break;
        }

        return true;
    }

    private Task KeyFrameWatchdogAsync() =>
        Media.KeyFrameWatchdog.RunAsync(
            _time,
            () => StreamInfo is not null,
            () => _receive.WaitingForKeyFrame,
            () => FrameNumber,
            _supervisor.QuietFor,
            RequestKeyFrameAsync,
            value => _stalled = value,
            _stop.Token);

    // The host is trusted to be well-formed, but a size that does not match the pixel data would read out of bounds.
    private static bool IsValidCursor(CursorShapeUpdate shape) =>
        shape.Width is > 0 and <= 512 && shape.Height is > 0 and <= 512 && shape.Bgra.Length == shape.Width * shape.Height * 4;

    private void OnStreamInfo(VideoStreamInfo info)
    {
        var width = (int)info.Width;
        var height = (int)info.Height;

        // Every stream info starts a new encoder on the host: the stream is reconfigured, or the host swapped its
        // agent (login screen, UAC) and continues with a fresh encoder. The supervisor refuses an unusable size and
        // replaces the decoder, so one from the old stream is never fed the new one, even at the same resolution.
        _supervisor.Start(width, height);
        _frames.Reset(width, height);
        StreamInfo = new DesktopStreamInfo(info.DisplayId, width, height, info.EncoderName, info.HardwareEncoder, _supervisor.Decoder!.Name);

        Displays = [.. info.Displays];
        _receive.WaitingForKeyFrame = true;
        StreamInfoChanged?.Invoke(this, EventArgs.Empty);
    }

    private async ValueTask WriteAsync(VideoControl message, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ProtobufFrames.WriteAsync(_channel.Stream, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }



    [LoggerMessage(Level = LogLevel.Warning, Message = "A new decoder could not be created; keeping the old one and requesting a key frame.")]
    private static partial void LogDecoderRebuildFailed(ILogger logger, Exception exception);




    [LoggerMessage(Level = LogLevel.Warning, Message = "A handler for the desktop viewer's Closed event threw.")]
    private static partial void LogCloseHandlerFailed(ILogger logger, Exception exception);
}
