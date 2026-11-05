using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Media;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Camera;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Features.Camera;

/// <summary>
/// Host side of the camera: lists the cameras, and streams one to the controller through the same encoding pipeline as
/// the desktop (NV12 → H.264 → video data frames, bitrate control, key-frame recovery). The dispatcher only lets this run
/// when the person at this computer allowed the camera, and only the ids this host offers can be opened.
/// </summary>
public sealed partial class CameraModule(
    ICameraEnumerator cameras,
    ICameraSourceFactory sources,
    IUserContextGuard guard,
    VideoCodecSelector codecs,
    TimeProvider time,
    ILogger<CameraModule> logger,
    ICameraAccess? cameraAccess = null) : IFeatureModule
{
    private const int MaxRequestBytes = 64 * 1024;
    private const int MaxFrameRate = CameraModes.MaxFrameRate;

    public ChannelType ChannelType => ChannelType.Camera;

    public Permissions RequiredPermission => Permissions.Camera;

    public async Task HandleAsync(IChannel channel, SessionContext session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(session);

        var stream = channel.Stream;
        try
        {
            OpenCamera? open = null;
            while (open is null)
            {
                var request = await ProtobufFrames.ReadAsync(stream, CameraRequest.Parser, MaxRequestBytes, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                if (guard.GetRefusalReason() is { } reason)
                {
                    await FailAsync(stream, CameraError.NotAllowed, reason, cancellationToken).ConfigureAwait(false);
                    return;
                }

                switch (request.BodyCase)
                {
                    case CameraRequest.BodyOneofCase.List:
                        // A list that is empty because the desktop was never granted the camera and a list that is
                        // empty because no camera is plugged in need different answers: the first is something the
                        // person at the host can fix with one click, the second is not. The controller would
                        // otherwise show the same "no cameras" sentence for both.
                        if (await CameraAccessRefusalAsync(cancellationToken).ConfigureAwait(false) is { } refusal)
                        {
                            await FailAsync(stream, CameraError.NotAllowed, refusal, cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        await SendAsync(stream, new CameraEvent { Cameras = Describe(cameras.GetCameras()) }, cancellationToken).ConfigureAwait(false);
                        break;
                    case CameraRequest.BodyOneofCase.Open:
                        open = request.Open;
                        break;
                    case CameraRequest.BodyOneofCase.KeyFrameRequest:
                        break;
                    default:
                        await FailAsync(stream, CameraError.Unsupported, "Open a camera first.", cancellationToken).ConfigureAwait(false);
                        return;
                }
            }

            await StreamAsync(stream, open, session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException)
        {
            // The controller went away, or the permission was withdrawn.
        }
    }

    /// <summary>
    /// The camera mode closest to what was asked for: same picture size preferred, then the nearest frame rate. Modes
    /// above 30 fps are only chosen when the camera offers nothing else; then the slowest ones are the candidates, and
    /// <see cref="StreamAsync"/> thins the stream down to 30 fps (the returned mode's rate is above 30 in that case).
    /// </summary>
    public static CameraMode? ChooseMode(IReadOnlyList<CameraMode> modes, int width, int height, int frameRate)
    {
        ArgumentNullException.ThrowIfNull(modes);
        var wanted = Math.Clamp(frameRate <= 0 ? MaxFrameRate : frameRate, 1, MaxFrameRate);
        return CameraModes.Usable(modes)
            .OrderBy(m => Math.Abs(((long)m.Width * m.Height) - ((long)width * height)))
            .ThenBy(m => Math.Abs(m.FrameRate - wanted))
            .Cast<CameraMode?>()
            .FirstOrDefault();
    }

    /// <summary>
    /// The sentence to send instead of a camera list when the desktop has not granted this host access, or <c>null</c>
    /// when listing should go ahead.
    /// </summary>
    private async Task<string?> CameraAccessRefusalAsync(CancellationToken cancellationToken)
    {
        if (cameraAccess is null)
        {
            // Platforms whose operating system asks for the camera when it is opened have no grant to read.
            return null;
        }

        CameraAccessState state;
        try
        {
            state = await cameraAccess.GetStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            // Not being able to read the grant is not the same as it being refused: listing still goes ahead, and an
            // enumeration that cannot reach the portal produces the ordinary empty list.
            return null;
        }

        return state switch
        {
            CameraAccessState.Unset or CameraAccessState.Denied =>
                "被控端未允许摄像头访问。请在被控端「高级设置 → 安全 → 摄像头权限」中允许后重试。",
            _ => null,
        };
    }

    private async Task StreamAsync(Stream stream, OpenCamera open, SessionContext session, CancellationToken cancellationToken)
    {
        var camera = cameras.GetCameras().FirstOrDefault(c => c.Id == open.CameraId);
        if (camera is null)
        {
            await FailAsync(stream, CameraError.NotFound, "That camera is not connected.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (ChooseMode(camera.Modes, (int)open.Width, (int)open.Height, (int)open.FrameRate) is not { } mode)
        {
            await FailAsync(stream, CameraError.Unavailable, "The camera reports no usable video format.", cancellationToken).ConfigureAwait(false);
            return;
        }

        // A camera that only offers more than 30 fps (60/120 fps cameras, capture cards) is streamed at 30: the
        // encoder, the bitrate control and the stream info all work with the rate that is actually sent.
        var streamRate = Math.Min(mode.FrameRate, MaxFrameRate);
        var limiter = mode.FrameRate > MaxFrameRate ? new FrameRateLimiter(MaxFrameRate) : null;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var state = new StreamState();
        var control = ReadControlAsync(stream, state, stop);
        IVideoEncoder? encoder = null;
        try
        {
            await using var source = sources.Open(camera.Id);
            LogOpened(logger, session.Id, camera.Name, mode.Width, mode.Height, mode.FrameRate);
            if (limiter is not null)
            {
                LogThinned(logger, session.Id, camera.Name, mode.FrameRate, streamRate);
            }

            var allowHardware = true;
            BitrateController? bitrate = null;
            var clock = System.Diagnostics.Stopwatch.StartNew();

            await foreach (var frame in source.CaptureAsync(mode, stop.Token).ConfigureAwait(false))
            {
                using (frame)
                {
                    // Dropped before any conversion or encoding. A pending key-frame request stays pending: it is
                    // only taken when a picture is actually encoded.
                    if (limiter is not null && !limiter.ShouldKeep(frame.Timestamp))
                    {
                        continue;
                    }

                    // Platforms do not agree on one camera format: Windows and Linux deliver NV12, AVFoundation
                    // delivers BGRA. The encoder only takes NV12, so a BGRA frame is converted here rather than
                    // dropped — the skip that used to stand here accepted the promise "cameras are NV12" and
                    // silently produced no picture at all on macOS. Same conversion, and the same even-dimension
                    // rounding, as the desktop capture path.
                    MemoryVideoFrame? converted = null;
                    VideoFrame picture;
                    if (frame.Format == PixelFormat.Nv12)
                    {
                        picture = frame;
                    }
                    else if (frame.Format == PixelFormat.Bgra32)
                    {
                        var width = frame.Width & ~1;
                        var height = frame.Height & ~1;
                        converted = new MemoryVideoFrame(width, height, PixelFormat.Nv12, frame.Timestamp);
                        ColorConversion.BgraToNv12(frame.GetPixels(out var stride), stride, width, height, converted.Buffer, converted.Stride);
                        picture = converted;
                    }
                    else
                    {
                        continue;
                    }

                    try
                    {
                        var capturedAt = time.GetUtcNow();
                        if (encoder is null || encoder.Options.Width != picture.Width || encoder.Options.Height != picture.Height)
                        {
                            encoder?.Dispose();
                            (encoder, bitrate) = CreateEncoder(picture.Width, picture.Height, streamRate, allowHardware);
                            await SendAsync(stream, new CameraEvent
                            {
                                StreamInfo = new CameraStreamInfo
                                {
                                    Width = (uint)picture.Width,
                                    Height = (uint)picture.Height,
                                    FrameRate = (uint)streamRate,
                                    CameraName = camera.Name,
                                    EncoderName = encoder.Name,
                                    HardwareEncoder = encoder.IsHardware,
                                },
                            }, stop.Token).ConfigureAwait(false);
                            state.KeyFrameRequested = true;
                        }

                        IReadOnlyList<EncodedVideoFrame> encoded;
                        try
                        {
                            encoded = encoder.Encode(picture, state.TakeKeyFrameRequest());
                        }
                        catch (Exception ex) when (encoder.IsHardware && ex is not OperationCanceledException)
                        {
                            // Drivers can fail mid-stream; continue in software from the next picture.
                            LogEncoderFailed(logger, encoder.Name, ex);
                            allowHardware = false;
                            encoder.Dispose();
                            encoder = null;
                            continue;
                        }

                        foreach (var unit in encoded)
                        {
                            var sendStarted = clock.Elapsed;
                            await VideoDataFrame.WriteAsync(stream, unit, capturedAt, stop.Token).ConfigureAwait(false);
                            if (bitrate!.OnFrameSent(clock.Elapsed - sendStarted, clock.Elapsed) is { } kbps)
                            {
                                encoder.SetBitrate(kbps);
                            }
                        }
                    }
                    finally
                    {
                        converted?.Dispose();
                    }
                }
            }
        }
        catch (CameraUnavailableException ex)
        {
            await FailAsync(stream, CameraError.Unavailable, ex.Message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            encoder?.Dispose();
            await control.ConfigureAwait(false);
            LogClosed(logger, session.Id, camera.Name);
        }
    }

    private (IVideoEncoder Encoder, BitrateController Bitrate) CreateEncoder(int width, int height, int frameRate, bool allowHardware)
    {
        // Natural video needs more bits per pixel than a mostly static desktop.
        var initialKbps = (int)Math.Clamp(width * height * frameRate * 0.1 / 1000, 500, 12_000);
        var options = new VideoEncoderOptions(VideoCodec.H264, width, height, frameRate, initialKbps, EncoderPreference.Smoothness);
        var encoder = codecs.CreateEncoder(options, allowHardware);
        LogEncoderCreated(logger, encoder.Name, encoder.IsHardware, width, height, frameRate, initialKbps);
        return (encoder, new BitrateController(initialKbps, Math.Max(250, initialKbps / 8), initialKbps * 2, frameRate));
    }

    private static CameraList Describe(IReadOnlyList<CameraInfo> cameras)
    {
        var list = new CameraList();
        foreach (var camera in cameras)
        {
            var description = new CameraDescription { Id = camera.Id, Name = camera.Name };
            description.Modes.Add(camera.Modes.Select(m => new CameraModeDescription { Width = (uint)m.Width, Height = (uint)m.Height, FrameRate = (uint)m.FrameRate }));
            list.Cameras.Add(description);
        }

        return list;
    }

    private static async Task ReadControlAsync(Stream stream, StreamState state, CancellationTokenSource stop)
    {
        try
        {
            while (await ProtobufFrames.ReadAsync(stream, CameraRequest.Parser, MaxRequestBytes, stop.Token).ConfigureAwait(false) is { } message)
            {
                if (message.BodyCase == CameraRequest.BodyOneofCase.KeyFrameRequest)
                {
                    state.KeyFrameRequested = true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
        {
        }

        // The controller closed its side: stop streaming (and release the camera).
        await stop.CancelAsync().ConfigureAwait(false);
    }

    private static ValueTask SendAsync(Stream stream, CameraEvent message, CancellationToken cancellationToken) =>
        ProtobufFrames.WriteAsync(stream, message, cancellationToken);

    private static ValueTask FailAsync(Stream stream, CameraError error, string message, CancellationToken cancellationToken) =>
        SendAsync(stream, new CameraEvent { Failed = new CameraFailed { Error = error, Message = message } }, cancellationToken);

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: camera '{Camera}' opened ({Width}x{Height}@{FrameRate}).")]
    private static partial void LogOpened(ILogger logger, SessionId sessionId, string camera, int width, int height, int frameRate);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: camera '{Camera}' only offers {CameraRate} fps; frames are dropped to {StreamRate} fps.")]
    private static partial void LogThinned(ILogger logger, SessionId sessionId, string camera, int cameraRate, int streamRate);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: camera '{Camera}' closed.")]
    private static partial void LogClosed(ILogger logger, SessionId sessionId, string camera);

    [LoggerMessage(Level = LogLevel.Information, Message = "Camera encoder {Encoder} (hardware: {Hardware}) {Width}x{Height}@{FrameRate}, {Kbps} kbps.")]
    private static partial void LogEncoderCreated(ILogger logger, string encoder, bool hardware, int width, int height, int frameRate, int kbps);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hardware encoder {Encoder} failed; falling back to software.")]
    private static partial void LogEncoderFailed(ILogger logger, string encoder, Exception exception);
}
