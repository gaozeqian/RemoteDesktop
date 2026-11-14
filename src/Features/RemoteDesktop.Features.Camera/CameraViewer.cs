using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Media;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Camera;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Features.Camera;

/// <summary>The host did not start the camera, and says why.</summary>
public sealed class CameraException : Exception
{
    public CameraException()
    {
    }

    public CameraException(string message)
        : base(message)
    {
    }

    public CameraException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CameraException(CameraError error, string message)
        : base(message)
    {
        Error = error;
    }

    public CameraError Error { get; }
}

public sealed record CameraDetails(string CameraName, int Width, int Height, int FrameRate, string EncoderName, bool HardwareEncoder, string DecoderName);

public sealed record CameraStatistics(double FramesPerSecond, double Kbps, double DecodeMilliseconds, double? LatencyMilliseconds);

/// <summary>
/// Controller side of the camera channel: opens one of the host's cameras, decodes the stream and keeps the newest
/// picture as BGRA for the UI to copy. Decoding runs on a background loop; <see cref="FrameReady"/> is raised from it.
/// </summary>
public sealed partial class CameraViewer : IAsyncDisposable
{
    private const int MaxEventBytes = 256 * 1024;

    /// <summary>
    /// How long the host has to answer the open request.
    /// </summary>
    /// <remarks>
    /// Deliberately longer than <see cref="RequestTimeout.Default"/>: opening a camera on the controlled end can be
    /// blocked on a TCC prompt that only a person standing at that machine can answer. The generic 30 seconds expired
    /// while the prompt was still up, so a denial arrived — or was about to arrive — as a misleading timeout instead
    /// of the host's own reason.
    /// </remarks>
    public static readonly TimeSpan OpenTimeout = TimeSpan.FromMinutes(2);

    private readonly IChannel _channel;
    private readonly VideoDecoderSupervisor _supervisor;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly VideoFrameBuffer _frames = new();
    private readonly VideoStatisticsMeter _meter = new();
    private Task _loop = Task.CompletedTask;
    private Task _watchdog = Task.CompletedTask;
    private volatile bool _waitingForKeyFrame = true;
    private volatile bool _stalled;
    private volatile string? _failure;

    private CameraViewer(IChannel channel, VideoCodecSelector codecs, TimeProvider time, ILogger logger)
    {
        _channel = channel;
        _time = time;
        _logger = logger;

        // The same clock as the watchdog tick, so a test can step silence forward instead of waiting two seconds.
        _supervisor = new VideoDecoderSupervisor(codecs, time, ex => LogDecoderRebuildFailed(_logger, ex));
    }

    public CameraDetails? Details { get; private set; }

    /// <summary>Total number of frames decoded so far. Zero means the stream is up but no picture has arrived.</summary>
    public long FrameNumber => _frames.FrameNumber;

    /// <summary>
    /// Whether the host stopped answering key-frame requests, so the picture is older than the window says. A camera
    /// never idles the way a desktop does, so this is a genuine stall rather than an unchanged screen.
    /// </summary>
    public bool IsStalled => _stalled;

    public CameraStatistics Statistics { get; private set; } = new(0, 0, 0, null);

    /// <summary>Why the stream ended, when the host (not the user) ended it: the camera was unplugged, taken by another program, or access was withdrawn.</summary>
    public string? Failure => _failure;

    /// <summary>Raised from the decode loop after a new picture is available via <see cref="CopyLatestFrame"/>.</summary>
    public event EventHandler? FrameReady;

    /// <summary>Raised when the stream restarts with another size (the host restarted its encoder).</summary>
    public event EventHandler? StreamChanged;

    /// <summary>Raised once when the stream ends (host stopped, connection lost, disposed). <see cref="Failure"/> may say why.</summary>
    public event EventHandler? Closed;

    /// <summary>The host's cameras and the modes they offer.</summary>
    /// <exception cref="CameraException">The host refused (the camera is switched off there, for example).</exception>
    public static async Task<IReadOnlyList<CameraInfo>> ListCamerasAsync(IPeerConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var channel = await connection.OpenChannelAsync(ChannelType.Camera, bidirectional: true, cancellationToken).ConfigureAwait(false);
        await using (channel.ConfigureAwait(false))
        {
            try
            {
                var answer = await RequestTimeout.RunAsync(
                    async token =>
                    {
                        await ProtobufFrames.WriteAsync(channel.Stream, new CameraRequest { List = new ListCamerasRequest() }, token).ConfigureAwait(false);
                        return await ReadEventAsync(channel.Stream, token).ConfigureAwait(false);
                    },
                    cancellationToken).ConfigureAwait(false);
                return answer.BodyCase switch
                {
                    CameraEvent.BodyOneofCase.Cameras =>
                    [
                        .. answer.Cameras.Cameras.Select(c => new CameraInfo(
                            c.Id,
                            c.Name,
                            [.. c.Modes.Select(m => new CameraMode((int)m.Width, (int)m.Height, (int)m.FrameRate))])),
                    ],
                    CameraEvent.BodyOneofCase.Failed => throw new CameraException(answer.Failed.Error, answer.Failed.Message),
                    _ => throw new CameraException(CameraError.Unsupported, "The host answered unexpectedly."),
                };
            }
            catch (TimeoutException ex)
            {
                throw new CameraException(ex.Message, ex);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                throw new CameraException("The connection to the host failed.", ex);
            }
        }
    }

    /// <summary>Starts streaming a camera and returns once the host has confirmed it and announced the picture size.</summary>
    /// <exception cref="CameraException">The host refused or could not open the camera.</exception>
    /// <exception cref="TimeoutException">
    /// The host did not answer within <see cref="OpenTimeout"/> — an unanswered permission prompt on the host is the
    /// ordinary way to get here, so the window turns this into advice rather than an error.
    /// </exception>
    public static async Task<CameraViewer> StartAsync(
        IPeerConnection connection,
        VideoCodecSelector codecs,
        string cameraId,
        CameraMode mode,
        TimeProvider time,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(mode);

        var channel = await connection.OpenChannelAsync(ChannelType.Camera, bidirectional: true, cancellationToken).ConfigureAwait(false);
        var viewer = new CameraViewer(channel, codecs, time, logger);
        try
        {
            await ProtobufFrames.WriteAsync(
                channel.Stream,
                new CameraRequest { Open = new OpenCamera { CameraId = cameraId, Width = (uint)mode.Width, Height = (uint)mode.Height, FrameRate = (uint)mode.FrameRate } },
                cancellationToken).ConfigureAwait(false);

            // The first answer is the stream description, or the reason there is none. Opening a camera can take a moment
            // on the host — a permission prompt may be waiting for somebody to walk up to it — so the deadline is the
            // shared one-shot one rather than anything tighter.
            var first = await RequestTimeout.RunAsync(token => ReadEventAsync(channel.Stream, token), cancellationToken, OpenTimeout).ConfigureAwait(false);
            switch (first.BodyCase)
            {
                case CameraEvent.BodyOneofCase.StreamInfo:
                    viewer.OnStreamInfo(first.StreamInfo);
                    break;
                case CameraEvent.BodyOneofCase.Failed:
                    throw new CameraException(first.Failed.Error, first.Failed.Message);
                default:
                    throw new CameraException(CameraError.Unsupported, "The host answered unexpectedly.");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            await viewer.DisposeAsync().ConfigureAwait(false);
            throw new CameraException("The connection to the host failed.", ex);
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

    public Task RequestKeyFrameAsync(CancellationToken cancellationToken) =>
        WriteAsync(new CameraRequest { KeyFrameRequest = new KeyFrameRequest() }, cancellationToken).AsTask();

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
        var clock = Stopwatch.StartNew();
        try
        {
            while (await FrameCodec.ReadAsync(_channel.Stream, cancellationToken: _stop.Token).ConfigureAwait(false) is { } frame)
            {
                if (frame.MessageType == ProtobufFrames.EnvelopeMessageType)
                {
                    var message = CameraEvent.Parser.ParseFrom(frame.Payload.Span);
                    if (message.BodyCase == CameraEvent.BodyOneofCase.StreamInfo)
                    {
                        OnStreamInfo(message.StreamInfo);
                        StreamChanged?.Invoke(this, EventArgs.Empty);
                    }
                    else if (message.BodyCase == CameraEvent.BodyOneofCase.Failed)
                    {
                        _failure = message.Failed.Message;
                    }

                    continue;
                }

                var decoder = _supervisor.Decoder;
                if (frame.MessageType != VideoDataFrame.MessageType || decoder is null)
                {
                    continue;
                }

                // A frame the peer sent malformed costs that frame, not the viewer: it is handled like a decode error.
                EncodedVideoFrame encoded;
                DateTimeOffset capturedAt;
                TimeSpan decodeStarted;
                VideoFrame? picture;
                try
                {
                    (encoded, capturedAt) = VideoDataFrame.Parse(frame);
                    if (_waitingForKeyFrame && !encoded.IsKeyFrame)
                    {
                        continue;
                    }

                    decodeStarted = clock.Elapsed;
                    picture = decoder.Decode(encoded);
                }
                catch (InvalidDataException ex)
                {
                    LogDecodeFailed(_logger, ex.Message);
                    _waitingForKeyFrame = true;
                    await RequestKeyFrameAsync(_stop.Token).ConfigureAwait(false);
                    continue;
                }

                _waitingForKeyFrame = false;
                if (picture is null)
                {
                    // The host is feeding non-key frames but the decoder stopped producing pictures: it has lost
                    // sync and is silently dropping everything. A camera never idles the way a desktop does, so this
                    // is a genuine stall rather than an unchanged screen; rebuild the decoder (this loop is its only
                    // owner) instead of leaving the watchdog to wait forever for a key frame it will never request.
                    //
                    // Whether it is a run of silence rather than one quiet answer is the supervisor's call.
                    if (_supervisor.RecordSilentFrame(encoded.IsKeyFrame))
                    {
                        LogDecoderStalled(_logger);
                        _supervisor.Rebuild();
                        _waitingForKeyFrame = true;
                        await RequestKeyFrameAsync(_stop.Token).ConfigureAwait(false);
                    }

                    continue;
                }

                using (picture)
                {
                    _frames.Publish(picture);
                }

                _supervisor.PictureShown();
                _stalled = false;

                var latencyMs = (_time.GetUtcNow() - capturedAt).TotalMilliseconds;
                _meter.Add(frame.Payload.Length, clock.Elapsed - decodeStarted, latencyMs);
                FrameReady?.Invoke(this, EventArgs.Empty);

                if (_meter.TryTake(out var snapshot))
                {
                    Statistics = new CameraStatistics(
                        snapshot.FramesPerSecond,
                        snapshot.Kbps,
                        snapshot.DecodeMilliseconds,
                        snapshot.LatencyMilliseconds);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The channel closed or the viewer was disposed: the ordinary way for the stream to end.
            LogStreamEnded(_logger, ex);
        }
        catch (Exception ex) when (ex is InvalidDataException or Google.Protobuf.InvalidProtocolBufferException)
        {
            // Nothing that can be skipped: an unusable stream description (or a control message that does not parse)
            // leaves no way to carry on, so the viewer ends, but not without saying why.
            LogStreamMalformed(_logger, ex);
        }
#pragma warning disable CA1031 // Decoder setup can fail in many backend-specific ways; the viewer just closes.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogViewerFailed(_logger, ex);
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

    private Task KeyFrameWatchdogAsync() =>
        Media.KeyFrameWatchdog.RunAsync(
            _time,
            () => Details is not null,
            () => _waitingForKeyFrame,
            () => FrameNumber,
            _supervisor.QuietFor,
            RequestKeyFrameAsync,
            value => _stalled = value,
            _stop.Token);

    private void OnStreamInfo(CameraStreamInfo info)
    {
        var width = (int)info.Width;
        var height = (int)info.Height;

        // A stream info always means a fresh encoder on the host; the old decoder must not be fed the new stream. The
        // supervisor refuses an unusable size (the size is whatever the other computer says it is) and replaces it.
        _supervisor.Start(width, height);
        _frames.Reset(width, height);
        Details = new CameraDetails(info.CameraName, width, height, (int)info.FrameRate, info.EncoderName, info.HardwareEncoder, _supervisor.Decoder!.Name);

        _waitingForKeyFrame = true;
    }

    private async ValueTask WriteAsync(CameraRequest message, CancellationToken cancellationToken)
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

    private static async Task<CameraEvent> ReadEventAsync(Stream stream, CancellationToken cancellationToken) =>
        await ProtobufFrames.ReadAsync(stream, CameraEvent.Parser, MaxEventBytes, cancellationToken).ConfigureAwait(false)
        ?? throw new CameraException("The host closed the channel.");

    [LoggerMessage(Level = LogLevel.Debug, Message = "Decoding failed ({Reason}); requested a key frame.")]
    private static partial void LogDecodeFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The camera decoder stopped producing pictures while being fed; rebuilding it and requesting a key frame.")]
    private static partial void LogDecoderStalled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A new camera decoder could not be created; keeping the old one and requesting a key frame.")]
    private static partial void LogDecoderRebuildFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The camera stream ended.")]
    private static partial void LogStreamEnded(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The camera stream ended because the host sent data that cannot be used.")]
    private static partial void LogStreamMalformed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Camera stream failed.")]
    private static partial void LogViewerFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A handler for the camera viewer's Closed event threw.")]
    private static partial void LogCloseHandlerFailed(ILogger logger, Exception exception);
}
