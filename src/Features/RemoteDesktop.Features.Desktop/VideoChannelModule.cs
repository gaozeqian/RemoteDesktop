using System.Diagnostics;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Media;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Desktop;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Features.Desktop;

/// <summary>
/// Streams one display to the controller: capture → BGRA→NV12 → H.264 → video channel, plus the pointer shape and
/// position so the controller can draw the real cursor itself. Used by both control and watch mode. The loop is
/// strictly sequential, so a slow network naturally lowers the frame rate (the capturer always hands out the newest
/// image) while the bitrate controller lowers quality. The controller can switch displays at any time, and monitors
/// may come and go: the loop follows both.
/// </summary>
public sealed partial class VideoChannelModule(
    IDisplayEnumerator displays,
    IScreenCapturerFactory capturers,
    VideoCodecSelector codecs,
    TimeProvider time,
    ILogger<VideoChannelModule> logger) : IFeatureModule
{
    private const int MaxFrameRate = 60;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DisplayPoll = TimeSpan.FromSeconds(2);

    /// <summary>Capture is rebuilt after this long without a frame, when the capturer also says it is broken.</summary>
    private static readonly TimeSpan NoFrameTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Rebuilds of a capturer that keeps failing are spaced out; each one costs a device and a duplication.</summary>
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(5);

    public ChannelType ChannelType => ChannelType.Video;

    public Permissions RequiredPermission => Permissions.ViewScreen;

    public async Task HandleAsync(IChannel channel, SessionContext session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        VideoRequest request;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(RequestTimeout);
            var first = await ProtobufFrames.ReadAsync(channel.Stream, VideoControl.Parser, 64 * 1024, timeout.Token).ConfigureAwait(false);
            if (first?.BodyCase != VideoControl.BodyOneofCase.Request)
            {
                return;
            }

            request = first.Request;
        }

        var available = displays.GetDisplays();
        var display = available.FirstOrDefault(d => d.Id == request.DisplayId) ?? Primary(available);
        if (display is null)
        {
            LogNoDisplay(logger, session.Id);
            return;
        }

        var frameRate = (int)Math.Clamp(request.MaxFrameRate == 0 ? MaxFrameRate : request.MaxFrameRate, 1, MaxFrameRate);
        var preference = request.Preference == QualityPreference.Quality ? EncoderPreference.Quality : EncoderPreference.Smoothness;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stream = new StreamState();
        var controlReader = ReadControlAsync(channel, stream, stop);
        try
        {
            await Task.Run(() => StreamAsync(channel, display, available, frameRate, preference, stream, session.Id, stop.Token), stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            // The controller went away.
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await controlReader.ConfigureAwait(false);
        }
    }

    private static DisplayInfo? Primary(IReadOnlyList<DisplayInfo> available) =>
        available.FirstOrDefault(d => d.IsPrimary) ?? (available.Count > 0 ? available[0] : null);

    private async Task StreamAsync(
        IChannel channel,
        DisplayInfo display,
        IReadOnlyList<DisplayInfo> available,
        int frameRate,
        EncoderPreference preference,
        StreamState stream,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var frameInterval = TimeSpan.FromSeconds(1.0 / frameRate);
        var allowHardware = true;
        IVideoEncoder? encoder = null;
        BitrateController? bitrate = null;
        var nextFrameDue = TimeSpan.Zero;
        var nextDisplayPoll = DisplayPoll;

        // The newest image is kept: on a static screen nothing new is captured, yet a controller that lost a frame
        // (or just joined) still needs a key frame to show anything.
        MemoryVideoFrame? latest = null;

        // Set when the newest image has not been encoded yet because it arrived before the frame-rate cap allowed.
        var pending = false;

        // Nothing to send is normal on a screen nobody is touching; a capturer that says it is broken while no
        // frames arrive is not, and that is the pair that used to leave the controller on a picture that never moved.
        var lastFrameAt = clock.Elapsed;
        var lastRecovery = TimeSpan.MinValue;
        var stalled = false;

        // The capturer raises its cursor events from inside CaptureAsync, i.e. on this loop; they are flushed to the
        // channel right after. Only the newest position matters; the shape is remembered to re-announce it on a new stream.
        var cursor = new CursorRelay();
        var capturer = capturers.Create(display);
        capturer.CursorChanged += cursor.OnShape;
        capturer.CursorMoved += cursor.OnMoved;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var switchTo = NextDisplay(stream, ref available, display, clock.Elapsed, ref nextDisplayPoll, out var listChanged);
                if (switchTo is not null && switchTo.Id != display.Id)
                {
                    var next = TryCreateCapturer(switchTo, sessionId);
                    if (next is not null)
                    {
                        capturer.CursorChanged -= cursor.OnShape;
                        capturer.CursorMoved -= cursor.OnMoved;
                        await capturer.DisposeAsync().ConfigureAwait(false);
                        capturer = next;
                        capturer.CursorChanged += cursor.OnShape;
                        capturer.CursorMoved += cursor.OnMoved;
                        display = switchTo;
                        LogDisplaySwitched(logger, sessionId, display.Id);

                        // A new stream (fresh encoder, fresh StreamInfo) tells the controller which display it is looking at.
                        latest?.Dispose();
                        latest = null;
                        encoder?.Dispose();
                        encoder = null;
                        pending = false;
                        listChanged = false;

                        // The new capturer gets the same grace as a session that has just started.
                        lastFrameAt = clock.Elapsed;
                        lastRecovery = TimeSpan.MinValue;
                    }
                }

                if (listChanged && encoder is not null)
                {
                    await SendStreamInfoAsync(channel, display, available, encoder, cursor, cancellationToken).ConfigureAwait(false);
                }

                // Pacing comes from the capture itself (it blocks until the screen changes, at most at the refresh
                // rate). Sleeping between frames would not work: timer resolution (~15.6 ms on Windows) turns a few
                // milliseconds of wait into a whole extra frame interval and halves the frame rate.
                var untilDue = nextFrameDue - clock.Elapsed;
                var timeout = pending ? TimeSpan.FromMilliseconds(Math.Max(1, untilDue.TotalMilliseconds)) : CaptureTimeout;
                using (var captured = await capturer.CaptureAsync(timeout, cancellationToken).ConfigureAwait(false))
                {
                    if (captured is not null)
                    {
                        lastFrameAt = clock.Elapsed;
                        if (stalled)
                        {
                            stalled = false;
                            LogCaptureRecovered(logger, sessionId, display.Id);
                        }

                        // H.264 in 4:2:0 needs even dimensions; an odd last row/column is dropped.
                        var width = captured.Width & ~1;
                        var height = captured.Height & ~1;
                        var nv12 = new MemoryVideoFrame(width, height, PixelFormat.Nv12, captured.Timestamp);
                        ColorConversion.BgraToNv12(captured.GetPixels(out var stride), stride, width, height, nv12.Buffer, nv12.Stride);
                        latest?.Dispose();
                        latest = nv12;
                        pending = true;
                    }
                }

                // The capturer is the only thing that can tell "a static screen" from "this display cannot be
                // captured any more", so only its own verdict is acted on; rebuilding a healthy one would cost a
                // device and a duplication every couple of seconds for nothing.
                if (capturer.IsFaulted && clock.Elapsed - lastFrameAt >= NoFrameTimeout
                    && (lastRecovery == TimeSpan.MinValue || clock.Elapsed - lastRecovery >= RecoveryInterval))
                {
                    lastRecovery = clock.Elapsed;
                    stalled = true;
                    LogCaptureStalled(logger, sessionId, display.Id);
                    capturer.Reset();

                    // Whatever `latest` holds predates the fault; the controller must not keep showing it as new.
                    latest?.Dispose();
                    latest = null;
                    pending = false;
                    stream.KeyFrameRequested = true;
                    await cursor.FlushAsync(channel.Stream, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await cursor.FlushAsync(channel.Stream, cancellationToken).ConfigureAwait(false);
                if (latest is null)
                {
                    continue;
                }

                // Encode the newest image once the cap allows it, or immediately when a key frame is needed. Frames up
                // to a quarter interval early count as due: refresh-rate jitter must not turn into a timer-sized wait.
                var due = clock.Elapsed + (frameInterval / 4) >= nextFrameDue;
                if (!(pending && due) && !stream.KeyFrameRequested)
                {
                    continue;
                }

                pending = false;

                var capturedAt = time.GetUtcNow();
                nextFrameDue = clock.Elapsed + frameInterval;

                if (encoder is null || encoder.Options.Width != latest.Width || encoder.Options.Height != latest.Height)
                {
                    encoder?.Dispose();
                    (encoder, bitrate) = CreateEncoder(latest.Width, latest.Height, frameRate, preference, allowHardware, sessionId);
                    await SendStreamInfoAsync(channel, display, available, encoder, cursor, cancellationToken).ConfigureAwait(false);
                    stream.KeyFrameRequested = true;
                }

                IReadOnlyList<EncodedVideoFrame> encoded;
                try
                {
                    encoded = encoder.Encode(latest, stream.TakeKeyFrameRequest());
                }
                catch (Exception ex) when (encoder.IsHardware && ex is not OperationCanceledException)
                {
                    // Drivers can fail mid-stream (device lost, session limits); continue in software.
                    LogEncoderFailed(logger, sessionId, encoder.Name, ex);
                    allowHardware = false;
                    encoder.Dispose();
                    encoder = null;
                    stream.KeyFrameRequested = true;
                    continue;
                }

                foreach (var unit in encoded)
                {
                    var sendStarted = clock.Elapsed;
                    await VideoDataFrame.WriteAsync(channel.Stream, unit, capturedAt, cancellationToken).ConfigureAwait(false);
                    if (bitrate!.OnFrameSent(clock.Elapsed - sendStarted, clock.Elapsed) is { } kbps)
                    {
                        encoder.SetBitrate(kbps);
                    }
                }
            }
        }
        finally
        {
            capturer.CursorChanged -= cursor.OnShape;
            capturer.CursorMoved -= cursor.OnMoved;
            await capturer.DisposeAsync().ConfigureAwait(false);
            encoder?.Dispose();
            latest?.Dispose();
        }
    }

    /// <summary>
    /// Decides whether to change display: the controller asked for one, or the current one vanished. Also refreshes
    /// the display list now and then, reporting whether it changed.
    /// </summary>
    private DisplayInfo? NextDisplay(
        StreamState stream,
        ref IReadOnlyList<DisplayInfo> available,
        DisplayInfo current,
        TimeSpan now,
        ref TimeSpan nextPoll,
        out bool listChanged)
    {
        listChanged = false;
        var requested = stream.TakeDisplayRequest();
        if (requested is null && now < nextPoll)
        {
            return null;
        }

        nextPoll = now + DisplayPoll;
        var fresh = displays.GetDisplays();
        if (fresh.Count > 0 && !fresh.SequenceEqual(available))
        {
            available = fresh;
            listChanged = true;
        }

        if (requested is not null && available.FirstOrDefault(d => d.Id == requested) is { } chosen)
        {
            return chosen;
        }

        return available.Any(d => d.Id == current.Id) ? null : Primary(available);
    }

    private IScreenCapturer? TryCreateCapturer(DisplayInfo display, SessionId sessionId)
    {
        try
        {
            return capturers.Create(display);
        }
#pragma warning disable CA1031 // The display may have been unplugged in the meantime; keep streaming the current one.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogSwitchFailed(logger, sessionId, display.Id, ex);
            return null;
        }
    }

    private (IVideoEncoder Encoder, BitrateController Bitrate) CreateEncoder(
        int width, int height, int frameRate, EncoderPreference preference, bool allowHardware, SessionId sessionId)
    {
        // ~0.06 bits per pixel per frame for smoothness, more for text-heavy "quality" sessions.
        var bitsPerPixel = preference == EncoderPreference.Quality ? 0.1 : 0.06;
        var initialKbps = (int)Math.Clamp(width * height * frameRate * bitsPerPixel / 1000, 1000, 40_000);
        var options = new VideoEncoderOptions(VideoCodec.H264, width, height, frameRate, initialKbps, preference);

        var encoder = codecs.CreateEncoder(options, allowHardware);
        LogEncoderCreated(logger, sessionId, encoder.Name, encoder.IsHardware, width, height, frameRate, initialKbps);
        return (encoder, new BitrateController(initialKbps, Math.Max(300, initialKbps / 8), initialKbps * 2, frameRate));
    }

    private static async ValueTask SendStreamInfoAsync(
        IChannel channel,
        DisplayInfo display,
        IReadOnlyList<DisplayInfo> available,
        IVideoEncoder encoder,
        CursorRelay cursor,
        CancellationToken cancellationToken)
    {
        var info = new VideoStreamInfo
        {
            Codec = VideoCodecMessage.VideoCodecH264,
            Width = (uint)encoder.Options.Width,
            Height = (uint)encoder.Options.Height,
            DisplayId = display.Id,
            EncoderName = encoder.Name,
            HardwareEncoder = encoder.IsHardware,
        };
        info.Displays.Add(available.Select(d => new DisplayDescription
        {
            Id = d.Id,
            Name = d.Name,
            Width = (uint)d.Bounds.Width,
            Height = (uint)d.Bounds.Height,
            Primary = d.IsPrimary,
        }));
        await ProtobufFrames.WriteAsync(channel.Stream, new VideoControl { StreamInfo = info }, cancellationToken).ConfigureAwait(false);

        // A new stream starts with the controller knowing no pointer yet.
        await cursor.ResendShapeAsync(channel.Stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReadControlAsync(IChannel channel, StreamState stream, CancellationTokenSource stop)
    {
        try
        {
            while (await ProtobufFrames.ReadAsync(channel.Stream, VideoControl.Parser, 64 * 1024, stop.Token).ConfigureAwait(false) is { } message)
            {
                switch (message.BodyCase)
                {
                    case VideoControl.BodyOneofCase.KeyFrameRequest:
                        stream.KeyFrameRequested = true;
                        break;
                    case VideoControl.BodyOneofCase.SelectDisplay:
                        stream.RequestDisplay(message.SelectDisplay.DisplayId);
                        break;
                    default:
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
        {
        }

        // The controller closed its side: stop streaming.
        await stop.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>Collects cursor events raised during a capture and writes the newest of each kind afterwards.</summary>
    private sealed class CursorRelay
    {
        private CursorShape? _pendingShape;
        private CursorShape? _lastShape;
        private CursorPosition? _pendingPosition;

        public void OnShape(object? sender, CursorShape shape)
        {
            _pendingShape = shape;
            _lastShape = shape;
        }

        public void OnMoved(object? sender, CursorPosition position) => _pendingPosition = position;

        public async ValueTask FlushAsync(Stream stream, CancellationToken cancellationToken)
        {
            if (_pendingShape is { } shape)
            {
                _pendingShape = null;
                await WriteShapeAsync(stream, shape, cancellationToken).ConfigureAwait(false);
            }

            if (_pendingPosition is { } position)
            {
                _pendingPosition = null;
                var update = new CursorPositionUpdate { X = position.X, Y = position.Y, Visible = position.Visible };
                await ProtobufFrames.WriteAsync(stream, new VideoControl { CursorPosition = update }, cancellationToken).ConfigureAwait(false);
            }
        }

        public async ValueTask ResendShapeAsync(Stream stream, CancellationToken cancellationToken)
        {
            _pendingShape = null;
            if (_lastShape is { } shape)
            {
                await WriteShapeAsync(stream, shape, cancellationToken).ConfigureAwait(false);
            }
        }

        private static ValueTask WriteShapeAsync(Stream stream, CursorShape shape, CancellationToken cancellationToken)
        {
            var update = new CursorShapeUpdate
            {
                Width = (uint)shape.Width,
                Height = (uint)shape.Height,
                HotspotX = shape.HotspotX,
                HotspotY = shape.HotspotY,
                Bgra = UnsafeByteOperations.UnsafeWrap(shape.Bgra),
            };
            return ProtobufFrames.WriteAsync(stream, new VideoControl { CursorShape = update }, cancellationToken);
        }
    }

    private sealed class StreamState
    {
        private volatile bool _keyFrameRequested;
        private volatile string? _requestedDisplay;

        public bool KeyFrameRequested
        {
            get => _keyFrameRequested;
            set => _keyFrameRequested = value;
        }

        public bool TakeKeyFrameRequest()
        {
            var requested = _keyFrameRequested;
            _keyFrameRequested = false;
            return requested;
        }

        public void RequestDisplay(string id) => _requestedDisplay = id;

        public string? TakeDisplayRequest() => Interlocked.Exchange(ref _requestedDisplay, null);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId}: no display available to stream.")]
    private static partial void LogNoDisplay(ILogger logger, SessionId sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: now streaming display {Display}.")]
    private static partial void LogDisplaySwitched(ILogger logger, SessionId sessionId, string display);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId}: could not switch to display {Display}; staying on the current one.")]
    private static partial void LogSwitchFailed(ILogger logger, SessionId sessionId, string display, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: video encoder {Encoder} (hardware: {Hardware}) {Width}x{Height}@{FrameRate}, {Kbps} kbps.")]
    private static partial void LogEncoderCreated(ILogger logger, SessionId sessionId, string encoder, bool hardware, int width, int height, int frameRate, int kbps);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId}: hardware encoder {Encoder} failed; falling back to software.")]
    private static partial void LogEncoderFailed(ILogger logger, SessionId sessionId, string encoder, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId}: no frames from display {Display} and the capturer reports a fault; rebuilding capture.")]
    private static partial void LogCaptureStalled(ILogger logger, SessionId sessionId, string display);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: display {Display} is delivering frames again.")]
    private static partial void LogCaptureRecovered(ILogger logger, SessionId sessionId, string display);
}
