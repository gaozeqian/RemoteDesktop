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

    /// <summary>No picture for this long and a key frame is asked for: the stream may simply have lost sync.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Non-key frames in a row that must come back without a picture before the decoder is judged out of sync (on top
    /// of <see cref="StallTimeout"/>). One quiet answer is ordinary; a run of them while the host keeps sending is not.
    /// </summary>
    private const int SilentFramesBeforeRebuild = 3;

    /// <summary>A key frame asked for and not answered for this long: what is on screen is old, not just unchanged.</summary>
    private static readonly TimeSpan StallAlarm = TimeSpan.FromSeconds(4);

    /// <summary>Retries stretch out to this, so a static host screen costs a handful of key frames, not a stream of them.</summary>
    private static readonly TimeSpan MaxKeyFrameBackoff = TimeSpan.FromSeconds(30);

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

    // Two buffers: the decode loop converts into _back outside the lock and swaps, while the UI copies _front under
    // the lock. Only the decode loop touches _back; _front is guarded by _frameLock.
    private byte[] _front = [];
    private byte[] _back = [];
    private long _frameNumber;
    private long _lastFrameAt = Stopwatch.GetTimestamp();
    private volatile bool _waitingForKeyFrame = true;
    private volatile bool _stalled;

    // Non-key frames in a row that produced no picture; only the receive loop touches it.
    private int _silentFrames;

    private DesktopViewer(IChannel channel, VideoCodecSelector codecs, TimeProvider time, ILogger logger)
    {
        _channel = channel;
        _codecs = codecs;
        _time = time;
        _logger = logger;
    }

    public DesktopStreamInfo? StreamInfo { get; private set; }

    public DesktopStatistics Statistics { get; private set; } = new(0, 0, 0, null);

    /// <summary>
    /// Whether the host has stopped answering key-frame requests, so the picture on screen is older than it claims
    /// to be. A static screen is not a stall: the host answers a request even when nothing changed.
    /// </summary>
    public bool IsStalled => _stalled;

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
                _front.AsSpan(y * rowBytes, rowBytes).CopyTo(destination.Slice(y * stride, rowBytes));
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

                // A frame the peer sent malformed costs that frame, not the viewer: it is handled like a decode error.
                EncodedVideoFrame encoded;
                DateTimeOffset capturedAt;
                TimeSpan decodeStarted;
                VideoFrame? picture;
                try
                {
                    (encoded, capturedAt) = VideoDataFrame.Parse(frame);
                    windowBytes += frame.Payload.Length;
                    if (_waitingForKeyFrame && !encoded.IsKeyFrame)
                    {
                        continue;
                    }

                    decodeStarted = clock.Elapsed;
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
                    // The host is feeding non-key frames but the decoder stopped producing pictures: it has lost
                    // sync and is silently dropping everything. Asking for one key frame only hides the problem
                    // until the next stall, so rebuild the decoder right here (this loop is its only owner) instead
                    // of leaving the watchdog to wait out the 30 s backoff.
                    //
                    // Only a run of non-key frames counts. A decoder may legitimately answer nothing for a unit that
                    // carries just parameter sets, or while it wants more input; a rebuild on the strength of one of
                    // those would throw away the SPS/PPS it just learned and leave the next picture undecodable.
                    if (!encoded.IsKeyFrame
                        && ++_silentFrames >= SilentFramesBeforeRebuild
                        && Stopwatch.GetElapsedTime(Volatile.Read(ref _lastFrameAt)) >= StallTimeout)
                    {
                        LogDecoderStalled(_logger);
                        RebuildDecoder();
                        _waitingForKeyFrame = true;
                        await RequestKeyFrameAsync(_stop.Token).ConfigureAwait(false);
                    }

                    continue;
                }

                _silentFrames = 0;

                using (picture)
                {
                    // Convert into the back buffer outside the lock: the UI copies the front buffer under the same
                    // lock, and a 4K colour conversion here must not serialize the two against each other.
                    ColorConversion.Nv12ToBgra(picture.GetPixels(out var stride), stride, picture.Width, picture.Height, _back, picture.Width * 4);

                    lock (_frameLock)
                    {
                        (_front, _back) = (_back, _front);
                        _frameNumber++;
                    }
                }

                // Any picture at all — including a key frame the host encoded from an unchanged screen — proves the
                // video path is alive, which is what the watchdog below measures.
                Volatile.Write(ref _lastFrameAt, Stopwatch.GetTimestamp());
                _stalled = false;

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
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    // While no picture can be shown (just joined, a decode error, or a stream that stopped mid-way), keep asking for
    // a key frame: the first request or key frame may have been lost, and a static host screen produces nothing new
    // on its own. The host answers from its newest picture even when nothing changed, so an answer proves the video
    // path is alive and an unanswered request proves it is not — which is the only way to tell "old picture" from
    // "unchanged picture" on this side.
    private async Task KeyFrameWatchdogAsync()
    {
        var backoff = KeyFrameRetry;
        var askedAt = 0L;
        var framesWhenAsked = 0L;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _time, _stop.Token).ConfigureAwait(false);
                if (StreamInfo is null)
                {
                    continue;
                }

                var now = Stopwatch.GetTimestamp();
                var quiet = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastFrameAt), now);
                if (quiet < StallTimeout)
                {
                    // More than the single answer to our request arrived: the screen is really changing, so the
                    // fast retry rate is right again. A static screen only ever produces that one answer, which is
                    // why it keeps backing off instead of asking for a key frame every couple of seconds.
                    if (FrameNumber - framesWhenAsked >= 2)
                    {
                        backoff = KeyFrameRetry;
                    }

                    continue;
                }

                if (_waitingForKeyFrame)
                {
                    // Nothing decodable has ever arrived: ask at a steady rate, there is no picture to lose.
                    if (askedAt == 0 || Stopwatch.GetElapsedTime(askedAt, now) >= KeyFrameRetry)
                    {
                        await RequestKeyFrameAsync(_stop.Token).ConfigureAwait(false);
                        askedAt = now;
                        framesWhenAsked = FrameNumber;
                    }

                    continue;
                }

                var answered = FrameNumber != framesWhenAsked;
                if (!answered && askedAt != 0 && Stopwatch.GetElapsedTime(askedAt, now) >= StallAlarm)
                {
                    // Asked and unanswered: the host has nothing to send, so what is on screen is stale.
                    _stalled = true;
                }

                if (askedAt != 0 && Stopwatch.GetElapsedTime(askedAt, now) < backoff)
                {
                    continue;
                }

                await RequestKeyFrameAsync(_stop.Token).ConfigureAwait(false);
                askedAt = now;
                framesWhenAsked = FrameNumber;
                backoff = TimeSpan.FromTicks(Math.Min(MaxKeyFrameBackoff.Ticks, backoff.Ticks * 2));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    // The host is trusted to be well-formed, but a size that does not match the pixel data would read out of bounds.
    private static bool IsValidCursor(CursorShapeUpdate shape) =>
        shape.Width is > 0 and <= 512 && shape.Height is > 0 and <= 512 && shape.Bgra.Length == shape.Width * shape.Height * 4;

    /// <summary>
    /// The largest stream this will set up buffers for. Every stream info allocates four bytes per pixel, and the
    /// size is whatever the peer says it is, so "the host is trusted to be well-formed" does not cover this: a host
    /// that is not really a host, or one that is confused, would otherwise have this window allocate a gigabyte on
    /// request, over and over. 8K UHD is the largest display there is, so nothing real is refused.
    /// </summary>
    private const int MaxDimension = 8192;

    private const long MaxPixels = 7680L * 4320;

    /// <summary>
    /// Replaces the decoder with a fresh one for the current stream. Called from the receive loop (the decoder's only
    /// owner) when the decoder keeps being fed yet stops producing pictures, so a lone key frame is not mistaken for
    /// a fixed stream.
    /// </summary>
    private void RebuildDecoder()
    {
        // Whatever happens, start counting again: a rebuild that failed must not be retried on the very next frame.
        _silentFrames = 0;
        Volatile.Write(ref _lastFrameAt, Stopwatch.GetTimestamp());

        if (StreamInfo is not { } info)
        {
            return;
        }

        // The new decoder comes first. Creating it can fail in backend-specific ways (no hardware decoder left, a
        // device that was lost); the old one is then still a decoder, and the caller's key frame request gives it a
        // chance, where a disposed one in _decoder would end the whole viewer on the next frame.
        IVideoDecoder fresh;
        try
        {
            fresh = _codecs.CreateDecoder(VideoCodec.H264, info.Width, info.Height);
        }
#pragma warning disable CA1031 // Any backend failure leaves the old decoder in place.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogDecoderRebuildFailed(_logger, ex);
            return;
        }

        var old = _decoder;
        _decoder = fresh;
        old?.Dispose();
    }

    private void OnStreamInfo(VideoStreamInfo info)
    {
        var width = (int)info.Width;
        var height = (int)info.Height;
        if (width < 2 || height < 2 || width > MaxDimension || height > MaxDimension || (long)width * height > MaxPixels)
        {
            throw new InvalidDataException($"Invalid stream size {width}x{height}.");
        }

        // Every stream info starts a new encoder on the host: the stream is reconfigured, or the host swapped its
        // agent (login screen, UAC) and continues with a fresh encoder. A decoder from the old stream must not be
        // fed the new one, even at the same resolution.
        _decoder?.Dispose();
        _decoder = _codecs.CreateDecoder(VideoCodec.H264, width, height);
        _silentFrames = 0;

        lock (_frameLock)
        {
            if (_front.Length != width * height * 4)
            {
                _front = new byte[width * height * 4];
                _back = new byte[width * height * 4];
            }

            _frameNumber = 0;
            StreamInfo = new DesktopStreamInfo(info.DisplayId, width, height, info.EncoderName, info.HardwareEncoder, _decoder.Name);
        }

        Displays = [.. info.Displays];
        Volatile.Write(ref _lastFrameAt, Stopwatch.GetTimestamp());
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "The decoder stopped producing pictures while being fed; rebuilding it and requesting a key frame.")]
    private static partial void LogDecoderStalled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A new decoder could not be created; keeping the old one and requesting a key frame.")]
    private static partial void LogDecoderRebuildFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The remote desktop stream ended.")]
    private static partial void LogStreamEnded(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The remote desktop stream ended because the host sent data that cannot be used.")]
    private static partial void LogStreamMalformed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Remote desktop stream failed.")]
    private static partial void LogViewerFailed(ILogger logger, Exception exception);
}
