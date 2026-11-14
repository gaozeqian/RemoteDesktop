using System.Diagnostics;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Media;
using RemoteDesktop.Protocol.Framing;

namespace RemoteDesktop.Features.Shared;

/// <summary>
/// The receive half both controller viewers run: read wire frames, hand the control envelopes to the viewer, decode
/// the video frames, and keep the newest picture. The only things that differ between a desktop and a camera stream
/// are the envelope type and what the viewer does with the picture, so those are the callbacks.
/// </summary>
/// <remarks>
/// A frame the peer sent malformed costs that frame, not the viewer: it is handled like a decode error, which asks
/// the host for a key frame. A decoder that keeps being fed and produces nothing is rebuilt after the supervisor's
/// silent-frame run, not left to the watchdog's longer timeout — the loop is the decoder's only owner, so it is the
/// only place that can replace it without a race.
/// </remarks>
public sealed partial class VideoReceiveLoop(
    Stream stream,
    VideoDecoderSupervisor supervisor,
    VideoFrameBuffer frames,
    VideoStatisticsMeter meter,
    TimeProvider time,
    Func<Frame, bool> handleEnvelope,
    Func<CancellationToken, Task> requestKeyFrame,
    Action pictureShown,
    Action<VideoStatisticsSnapshot> statisticsUpdated,
    Action frameReady,
    ILogger logger)
{
    private volatile bool _waitingForKeyFrame = true;

    /// <summary>
    /// Whether the stream is waiting for a key frame. The watchdog reads it; a new stream description sets it, because
    /// the decoder that follows is new and cannot use the deltas of the stream it replaced.
    /// </summary>
    public bool WaitingForKeyFrame
    {
        get => _waitingForKeyFrame;
        set => _waitingForKeyFrame = value;
    }

    /// <summary>Runs until the stream ends, the token fires or the channel closes.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            while (await FrameCodec.ReadAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false) is { } frame)
            {
                if (handleEnvelope(frame))
                {
                    continue;
                }

                var decoder = supervisor.Decoder;
                if (frame.MessageType != VideoDataFrame.MessageType || decoder is null)
                {
                    continue;
                }

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
                    LogDecodeFailed(logger, ex.Message);
                    _waitingForKeyFrame = true;
                    await requestKeyFrame(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _waitingForKeyFrame = false;
                if (picture is null)
                {
                    // The host is feeding non-key frames but the decoder stopped producing pictures: it has lost
                    // sync and is silently dropping everything. Asking for one key frame only hides the problem
                    // until the next stall, so rebuild the decoder right here (this loop is its only owner) instead
                    // of leaving the watchdog to wait out the backoff.
                    if (supervisor.RecordSilentFrame(encoded.IsKeyFrame))
                    {
                        LogDecoderStalled(logger);
                        supervisor.Rebuild();
                        _waitingForKeyFrame = true;
                        await requestKeyFrame(cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                using (picture)
                {
                    frames.Publish(picture);
                }

                // Any picture at all proves the video path is alive, which is what the watchdog measures.
                supervisor.PictureShown();
                pictureShown();

                var latencyMs = (time.GetUtcNow() - capturedAt).TotalMilliseconds;
                meter.Add(frame.Payload.Length, clock.Elapsed - decodeStarted, latencyMs);
                frameReady();

                if (meter.TryTake(out var snapshot))
                {
                    statisticsUpdated(snapshot);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The channel closed or the viewer was disposed: the ordinary way for the stream to end.
            LogStreamEnded(logger, ex);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidProtocolBufferException)
        {
            // Nothing that can be skipped: an unusable stream description (or a control message that does not parse)
            // leaves no way to carry on, so the viewer ends, but not without saying why.
            LogStreamMalformed(logger, ex);
        }
#pragma warning disable CA1031 // Decoder setup can fail in many backend-specific ways; the viewer just closes.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogViewerFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Decoding failed ({Reason}); requested a key frame.")]
    private static partial void LogDecodeFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The decoder stopped producing pictures while being fed; rebuilding it and requesting a key frame.")]
    private static partial void LogDecoderStalled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The video stream ended.")]
    private static partial void LogStreamEnded(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The video stream ended because the host sent data that cannot be used.")]
    private static partial void LogStreamMalformed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Video stream failed.")]
    private static partial void LogViewerFailed(ILogger logger, Exception exception);
}
