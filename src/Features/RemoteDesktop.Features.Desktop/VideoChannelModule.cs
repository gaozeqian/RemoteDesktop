using System.Diagnostics;
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
/// Streams one display to the controller: capture → BGRA→NV12 → H.264 → video channel. Used by both control and
/// watch mode. The loop is strictly sequential, so a slow network naturally lowers the frame rate (the capturer
/// always hands out the newest image) while the bitrate controller lowers quality.
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
        var display = available.FirstOrDefault(d => d.Id == request.DisplayId) ?? available.FirstOrDefault(d => d.IsPrimary) ?? (available.Count > 0 ? available[0] : null);
        if (display is null)
        {
            LogNoDisplay(logger);
            return;
        }

        var frameRate = (int)Math.Clamp(request.MaxFrameRate == 0 ? MaxFrameRate : request.MaxFrameRate, 1, MaxFrameRate);
        var preference = request.Preference == QualityPreference.Quality ? EncoderPreference.Quality : EncoderPreference.Smoothness;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stream = new StreamState();
        var controlReader = ReadControlAsync(channel, stream, stop);
        try
        {
            await Task.Run(() => StreamAsync(channel, display, available, frameRate, preference, stream, stop.Token), stop.Token).ConfigureAwait(false);
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

    private async Task StreamAsync(
        IChannel channel,
        DisplayInfo display,
        IReadOnlyList<DisplayInfo> available,
        int frameRate,
        EncoderPreference preference,
        StreamState stream,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var frameInterval = TimeSpan.FromSeconds(1.0 / frameRate);
        var allowHardware = true;
        IVideoEncoder? encoder = null;
        BitrateController? bitrate = null;
        var nextFrameDue = TimeSpan.Zero;

        // The newest image is kept: on a static screen nothing new is captured, yet a controller that lost a frame
        // (or just joined) still needs a key frame to show anything.
        MemoryVideoFrame? latest = null;

        // Set when the newest image has not been encoded yet because it arrived before the frame-rate cap allowed.
        var pending = false;

        await using var capturer = capturers.Create(display);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Pacing comes from the capture itself (it blocks until the screen changes, at most at the refresh
                // rate). Sleeping between frames would not work: timer resolution (~15.6 ms on Windows) turns a few
                // milliseconds of wait into a whole extra frame interval and halves the frame rate.
                var untilDue = nextFrameDue - clock.Elapsed;
                var timeout = pending ? TimeSpan.FromMilliseconds(Math.Max(1, untilDue.TotalMilliseconds)) : CaptureTimeout;
                using (var captured = await capturer.CaptureAsync(timeout, cancellationToken).ConfigureAwait(false))
                {
                    if (captured is not null)
                    {
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
                    (encoder, bitrate) = CreateEncoder(latest.Width, latest.Height, frameRate, preference, allowHardware);
                    await SendStreamInfoAsync(channel, display, available, encoder, cancellationToken).ConfigureAwait(false);
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
                    LogEncoderFailed(logger, encoder.Name, ex);
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
            encoder?.Dispose();
            latest?.Dispose();
        }
    }

    private (IVideoEncoder Encoder, BitrateController Bitrate) CreateEncoder(int width, int height, int frameRate, EncoderPreference preference, bool allowHardware)
    {
        // ~0.06 bits per pixel per frame for smoothness, more for text-heavy "quality" sessions.
        var bitsPerPixel = preference == EncoderPreference.Quality ? 0.1 : 0.06;
        var initialKbps = (int)Math.Clamp(width * height * frameRate * bitsPerPixel / 1000, 1000, 40_000);
        var options = new VideoEncoderOptions(VideoCodec.H264, width, height, frameRate, initialKbps, preference);

        var encoder = codecs.CreateEncoder(options, allowHardware);
        LogEncoderCreated(logger, encoder.Name, encoder.IsHardware, width, height, frameRate, initialKbps);
        return (encoder, new BitrateController(initialKbps, Math.Max(300, initialKbps / 8), initialKbps * 2, frameRate));
    }

    private static ValueTask SendStreamInfoAsync(IChannel channel, DisplayInfo display, IReadOnlyList<DisplayInfo> available, IVideoEncoder encoder, CancellationToken cancellationToken)
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
        return ProtobufFrames.WriteAsync(channel.Stream, new VideoControl { StreamInfo = info }, cancellationToken);
    }

    private static async Task ReadControlAsync(IChannel channel, StreamState stream, CancellationTokenSource stop)
    {
        try
        {
            while (await ProtobufFrames.ReadAsync(channel.Stream, VideoControl.Parser, 64 * 1024, stop.Token).ConfigureAwait(false) is { } message)
            {
                if (message.BodyCase == VideoControl.BodyOneofCase.KeyFrameRequest)
                {
                    stream.KeyFrameRequested = true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
        {
        }

        // The controller closed its side: stop streaming.
        await stop.CancelAsync().ConfigureAwait(false);
    }

    private sealed class StreamState
    {
        private volatile bool _keyFrameRequested;

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
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No display available to stream.")]
    private static partial void LogNoDisplay(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Video encoder {Encoder} (hardware: {Hardware}) {Width}x{Height}@{FrameRate}, {Kbps} kbps.")]
    private static partial void LogEncoderCreated(ILogger logger, string encoder, bool hardware, int width, int height, int frameRate, int kbps);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hardware encoder {Encoder} failed; falling back to software.")]
    private static partial void LogEncoderFailed(ILogger logger, string encoder, Exception exception);
}
