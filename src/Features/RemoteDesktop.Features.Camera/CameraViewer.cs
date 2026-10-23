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
    private static readonly TimeSpan StatisticsWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan KeyFrameRetry = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(30);
    private const int MaxEventBytes = 256 * 1024;

    private readonly IChannel _channel;
    private readonly VideoCodecSelector _codecs;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Lock _frameLock = new();
    private Task _loop = Task.CompletedTask;
    private Task _watchdog = Task.CompletedTask;
    private IVideoDecoder? _decoder;
    private byte[] _bgra = [];
    private long _frameNumber;
    private volatile bool _waitingForKeyFrame = true;
    private volatile string? _failure;

    private CameraViewer(IChannel channel, VideoCodecSelector codecs, TimeProvider time, ILogger logger)
    {
        _channel = channel;
        _codecs = codecs;
        _time = time;
        _logger = logger;
    }

    public CameraDetails? Details { get; private set; }

    /// <summary>Total number of frames decoded so far. Zero means the stream is up but no picture has arrived.</summary>
    public long FrameNumber
    {
        get
        {
            lock (_frameLock)
            {
                return _frameNumber;
            }
        }
    }

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
                await ProtobufFrames.WriteAsync(channel.Stream, new CameraRequest { List = new ListCamerasRequest() }, cancellationToken).ConfigureAwait(false);
                var answer = await ReadEventAsync(channel.Stream, cancellationToken).ConfigureAwait(false);
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
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                throw new CameraException("The connection to the host failed.", ex);
            }
        }
    }

    /// <summary>Starts streaming a camera and returns once the host has confirmed it and announced the picture size.</summary>
    /// <exception cref="CameraException">The host refused or could not open the camera.</exception>
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

            // The first answer is the stream description, or the reason there is none. Opening a camera can take a moment.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(OpenTimeout);
            var first = await ReadEventAsync(channel.Stream, timeout.Token).ConfigureAwait(false);
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
    public long CopyLatestFrame(Span<byte> destination, int stride, int width, int height)
    {
        lock (_frameLock)
        {
            var info = Details;
            if (info is null || _frameNumber == 0 || info.Width != width || info.Height != height)
            {
                return -1;
            }

            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
            {
                _bgra.AsSpan(y * rowBytes, rowBytes).CopyTo(destination.Slice(y * stride, rowBytes));
            }

            return _frameNumber;
        }
    }

    public Task RequestKeyFrameAsync(CancellationToken cancellationToken) =>
        WriteAsync(new CameraRequest { KeyFrameRequest = new KeyFrameRequest() }, cancellationToken).AsTask();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _channel.DisposeAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        await _watchdog.ConfigureAwait(false);
        _decoder?.Dispose();
        _stop.Dispose();
        _writeLock.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        var clock = Stopwatch.StartNew();
        var windowStart = TimeSpan.Zero;
        var windowFrames = 0;
        long windowBytes = 0;
        double windowDecodeMs = 0;
        double? latencyMs = null;
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

                if (frame.MessageType != VideoDataFrame.MessageType || _decoder is null)
                {
                    continue;
                }

                var (encoded, capturedAt) = VideoDataFrame.Parse(frame);
                windowBytes += frame.Payload.Length;
                if (_waitingForKeyFrame && !encoded.IsKeyFrame)
                {
                    continue;
                }

                var decodeStarted = clock.Elapsed;
                VideoFrame? picture;
                try
                {
                    picture = _decoder.Decode(encoded);
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
                    continue;
                }

                using (picture)
                {
                    lock (_frameLock)
                    {
                        ColorConversion.Nv12ToBgra(picture.GetPixels(out var stride), stride, picture.Width, picture.Height, _bgra, picture.Width * 4);
                        _frameNumber++;
                    }
                }

                windowDecodeMs += (clock.Elapsed - decodeStarted).TotalMilliseconds;
                windowFrames++;
                latencyMs = (_time.GetUtcNow() - capturedAt).TotalMilliseconds;
                FrameReady?.Invoke(this, EventArgs.Empty);

                var elapsed = clock.Elapsed - windowStart;
                if (elapsed >= StatisticsWindow)
                {
                    Statistics = new CameraStatistics(
                        windowFrames / elapsed.TotalSeconds,
                        windowBytes * 8 / elapsed.TotalSeconds / 1000,
                        windowFrames == 0 ? 0 : windowDecodeMs / windowFrames,
                        latencyMs);
                    windowStart = clock.Elapsed;
                    windowFrames = 0;
                    windowBytes = 0;
                    windowDecodeMs = 0;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException
                                       or Google.Protobuf.InvalidProtocolBufferException)
        {
        }
#pragma warning disable CA1031 // Decoder setup can fail in many backend-specific ways; the viewer just closes.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogViewerFailed(_logger, ex);
        }
        finally
        {
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    // While no picture can be shown (just joined, or a decode error), keep asking for a key frame: the first request
    // or key frame may have been lost.
    private async Task KeyFrameWatchdogAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(KeyFrameRetry, _time, _stop.Token).ConfigureAwait(false);
                if (Details is not null && _waitingForKeyFrame)
                {
                    await RequestKeyFrameAsync(_stop.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// The largest camera stream this will set up buffers for. The same argument as
    /// <c>DesktopViewer.MaxDimension</c>: every stream info allocates four bytes per pixel, and the size is whatever
    /// the other computer says it is. A camera that really produces 8K UHD is already at the edge of what exists;
    /// 16384x16384 would be a gigabyte allocated on request, and alternating sizes would do it again on every
    /// StreamInfo. 8K is far past any camera anybody points this at.
    /// </summary>
    private const int MaxDimension = 8192;

    private const long MaxPixels = 7680L * 4320;

    private void OnStreamInfo(CameraStreamInfo info)
    {
        var width = (int)info.Width;
        var height = (int)info.Height;
        if (width < 2 || height < 2 || width > MaxDimension || height > MaxDimension || (long)width * height > MaxPixels)
        {
            throw new InvalidDataException($"Invalid stream size {width}x{height}.");
        }

        // A stream info always means a fresh encoder on the host; the old decoder must not be fed the new stream.
        _decoder?.Dispose();
        _decoder = _codecs.CreateDecoder(VideoCodec.H264, width, height);
        lock (_frameLock)
        {
            if (_bgra.Length != width * height * 4)
            {
                _bgra = new byte[width * height * 4];
            }

            _frameNumber = 0;
            Details = new CameraDetails(info.CameraName, width, height, (int)info.FrameRate, info.EncoderName, info.HardwareEncoder, _decoder.Name);
        }

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

    [LoggerMessage(Level = LogLevel.Error, Message = "Camera stream failed.")]
    private static partial void LogViewerFailed(ILogger logger, Exception exception);
}
