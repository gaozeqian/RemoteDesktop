using System.Diagnostics;

namespace RemoteDesktop.Media;

/// <summary>
/// While no picture can be shown (just joined, a decode error, or a stream that stopped mid-way), keeps asking for a
/// key frame: the first request or key frame may have been lost, and a static host screen produces nothing new on its
/// own. An answer proves the video path is alive and an unanswered request proves it is not — which is the only way to
/// tell "old picture" from "unchanged picture" on this side.
/// </summary>
/// <remarks>
/// The desktop viewer had this logic and the camera viewer did not, so the camera asked for a key frame every two
/// seconds for ever and never reported a stall. It takes the state it needs through delegates because the two viewers
/// keep their own stream types.
/// </remarks>
public static class KeyFrameWatchdog
{
    private static readonly TimeSpan KeyFrameRetry = TimeSpan.FromSeconds(2);

    /// <summary>A key frame asked for and not answered for this long: what is on screen is old, not just unchanged.</summary>
    private static readonly TimeSpan StallAlarm = TimeSpan.FromSeconds(4);

    /// <summary>Retries stretch out to this, so a static host screen costs a handful of key frames, not a stream of them.</summary>
    private static readonly TimeSpan MaxKeyFrameBackoff = TimeSpan.FromSeconds(30);

    public static async Task RunAsync(
        TimeProvider time,
        Func<bool> hasStream,
        Func<bool> waitingForKeyFrame,
        Func<long> frameNumber,
        Func<long, TimeSpan> quietFor,
        Func<CancellationToken, Task> requestKeyFrame,
        Action<bool> stalled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(hasStream);
        ArgumentNullException.ThrowIfNull(waitingForKeyFrame);
        ArgumentNullException.ThrowIfNull(frameNumber);
        ArgumentNullException.ThrowIfNull(quietFor);
        ArgumentNullException.ThrowIfNull(requestKeyFrame);
        ArgumentNullException.ThrowIfNull(stalled);

        var backoff = KeyFrameRetry;
        var askedAt = 0L;
        var framesWhenAsked = 0L;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), time, cancellationToken).ConfigureAwait(false);
                if (!hasStream())
                {
                    continue;
                }

                var now = Stopwatch.GetTimestamp();
                var quiet = quietFor(now);
                if (quiet < VideoDecoderSupervisor.StallTimeout)
                {
                    // More than the single answer to our request arrived: the screen is really changing, so the
                    // fast retry rate is right again. A static screen only ever produces that one answer, which is
                    // why it keeps backing off instead of asking for a key frame every couple of seconds.
                    if (frameNumber() - framesWhenAsked >= 2)
                    {
                        backoff = KeyFrameRetry;
                    }

                    continue;
                }

                if (waitingForKeyFrame())
                {
                    // Nothing decodable has ever arrived: ask at a steady rate, there is no picture to lose.
                    if (askedAt == 0 || Stopwatch.GetElapsedTime(askedAt, now) >= KeyFrameRetry)
                    {
                        await requestKeyFrame(cancellationToken).ConfigureAwait(false);
                        askedAt = now;
                        framesWhenAsked = frameNumber();
                    }

                    continue;
                }

                var answered = frameNumber() != framesWhenAsked;
                if (!answered && askedAt != 0 && Stopwatch.GetElapsedTime(askedAt, now) >= StallAlarm)
                {
                    // Asked and unanswered: the host has nothing to send, so what is on screen is stale.
                    stalled(true);
                }

                if (askedAt != 0 && Stopwatch.GetElapsedTime(askedAt, now) < backoff)
                {
                    continue;
                }

                await requestKeyFrame(cancellationToken).ConfigureAwait(false);
                askedAt = now;
                framesWhenAsked = frameNumber();
                backoff = TimeSpan.FromTicks(Math.Min(MaxKeyFrameBackoff.Ticks, backoff.Ticks * 2));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }
}
