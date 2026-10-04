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
    ILogger<CameraModule> logger) : IFeatureModule
{
    private const int MaxRequestBytes = 64 * 1024;
    private const int MaxFrameRate = 30;

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

    /// <summary>The camera mode closest to what was asked for: same picture size preferred, then the nearest frame rate.</summary>
    public static CameraMode? ChooseMode(IReadOnlyList<CameraMode> modes, int width, int height, int frameRate)
    {
        ArgumentNullException.ThrowIfNull(modes);
        var wanted = Math.Clamp(frameRate <= 0 ? MaxFrameRate : frameRate, 1, MaxFrameRate);
        return modes
            .Where(m => m.FrameRate <= MaxFrameRate)
            .OrderBy(m => Math.Abs(((long)m.Width * m.Height) - ((long)width * height)))
            .ThenBy(m => Math.Abs(m.FrameRate - wanted))
            .Cast<CameraMode?>()
            .FirstOrDefault();
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

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var state = new StreamState();
        var control = ReadControlAsync(stream, state, stop);
        IVideoEncoder? encoder = null;
        try
        {
            await using var source = sources.Open(camera.Id);
            LogOpened(logger, session.Id, camera.Name, mode.Width, mode.Height, mode.FrameRate);
            var allowHardware = true;
            BitrateController? bitrate = null;
            var clock = System.Diagnostics.Stopwatch.StartNew();

            await foreach (var frame in source.CaptureAsync(mode, stop.Token).ConfigureAwait(false))
            {
                using (frame)
                {
                    if (frame.Format != PixelFormat.Nv12)
                    {
                        continue; // the ports promise NV12 for cameras; anything else cannot be encoded here
                    }

                    var capturedAt = time.GetUtcNow();
                    if (encoder is null || encoder.Options.Width != frame.Width || encoder.Options.Height != frame.Height)
                    {
                        encoder?.Dispose();
                        (encoder, bitrate) = CreateEncoder(frame.Width, frame.Height, mode.FrameRate, allowHardware);
                        await SendAsync(stream, new CameraEvent
                        {
                            StreamInfo = new CameraStreamInfo
                            {
                                Width = (uint)frame.Width,
                                Height = (uint)frame.Height,
                                FrameRate = (uint)mode.FrameRate,
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
                        encoded = encoder.Encode(frame, state.TakeKeyFrameRequest());
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: camera '{Camera}' closed.")]
    private static partial void LogClosed(ILogger logger, SessionId sessionId, string camera);

    [LoggerMessage(Level = LogLevel.Information, Message = "Camera encoder {Encoder} (hardware: {Hardware}) {Width}x{Height}@{FrameRate}, {Kbps} kbps.")]
    private static partial void LogEncoderCreated(ILogger logger, string encoder, bool hardware, int width, int height, int frameRate, int kbps);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hardware encoder {Encoder} failed; falling back to software.")]
    private static partial void LogEncoderFailed(ILogger logger, string encoder, Exception exception);
}
