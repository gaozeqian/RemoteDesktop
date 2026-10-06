using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Media;
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
    private static readonly TimeSpan StatisticsWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan KeyFrameRetry = TimeSpan.FromSeconds(2);

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

    private DesktopViewer(IChannel channel, VideoCodecSelector codecs, TimeProvider time, ILogger logger)
    {
        _channel = channel;
        _codecs = codecs;
        _time = time;
        _logger = logger;
    }

    public DesktopStreamInfo? StreamInfo { get; private set; }

    public DesktopStatistics Statistics { get; private set; } = new(0, 0, 0, null);

    /// <summary>Total number of frames decoded so far.</summary>
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
    public long CopyLatestFrame(Span<byte> destination, int stride, int width, int height)
    {
        lock (_frameLock)
        {
            var info = StreamInfo;
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
        _decoder?.Dispose();
        _stop.Dispose();
        _writeLock.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        var clock = Stopwatch.StartNew();
        var windowStart = TimeSpan.Zero;
        int windowFrames = 0;
        long windowBytes = 0;
        double windowDecodeMs = 0;
        double? latencyMs = null;
        try
        {
            while (await FrameCodec.ReadAsync(_channel.Stream, cancellationToken: _stop.Token).ConfigureAwait(false) is { } frame)
            {
                if (frame.MessageType == ProtobufFrames.EnvelopeMessageType)
                {
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
                    Statistics = new DesktopStatistics(
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

    // While no picture can be shown (just joined, or a decode error), keep asking for a key frame: the first
    // request or key frame may have been lost, and a static host screen produces nothing new on its own.
    private async Task KeyFrameWatchdogAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(KeyFrameRetry, _time, _stop.Token).ConfigureAwait(false);
                if (StreamInfo is not null && _waitingForKeyFrame)
                {
                    await RequestKeyFrameAsync(_stop.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    // The host is trusted to be well-formed, but a size that does not match the pixel data would read out of bounds.
    private static bool IsValidCursor(CursorShapeUpdate shape) =>
        shape.Width is > 0 and <= 512 && shape.Height is > 0 and <= 512 && shape.Bgra.Length == shape.Width * shape.Height * 4;

    private void OnStreamInfo(VideoStreamInfo info)
    {
        var width = (int)info.Width;
        var height = (int)info.Height;
        if (width < 2 || height < 2 || width > 16384 || height > 16384)
        {
            throw new InvalidDataException($"Invalid stream size {width}x{height}.");
        }

        // Every stream info starts a new encoder on the host: the stream is reconfigured, or the host swapped its
        // agent (login screen, UAC) and continues with a fresh encoder. A decoder from the old stream must not be
        // fed the new one, even at the same resolution.
        _decoder?.Dispose();
        _decoder = _codecs.CreateDecoder(VideoCodec.H264, width, height);

        lock (_frameLock)
        {
            if (_bgra.Length != width * height * 4)
            {
                _bgra = new byte[width * height * 4];
            }

            _frameNumber = 0;
            StreamInfo = new DesktopStreamInfo(info.DisplayId, width, height, info.EncoderName, info.HardwareEncoder, _decoder.Name);
        }

        Displays = [.. info.Displays];
        _waitingForKeyFrame = true;
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Decoding failed ({Reason}); requested a key frame.")]
    private static partial void LogDecodeFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Remote desktop stream failed.")]
    private static partial void LogViewerFailed(ILogger logger, Exception exception);
}
