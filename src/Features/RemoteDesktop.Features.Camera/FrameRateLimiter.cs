namespace RemoteDesktop.Features.Camera;

/// <summary>
/// Thins a stream of pictures down to at most a given number of frames per second, using the pictures' own
/// timestamps. Kept pictures are placed on a fixed grid of <c>1 / maxFrameRate</c> seconds, so the long-run rate does
/// not exceed the limit, a picture that comes a little early or late still lands on its slot (no over-dropping on
/// uneven input), and a gap in the input is not made up for with a burst afterwards. The first picture is always kept.
/// </summary>
public sealed class FrameRateLimiter
{
    private readonly TimeSpan _interval;
    private readonly TimeSpan _tolerance;
    private TimeSpan _nextDue;
    private TimeSpan? _lastSeen;

    public FrameRateLimiter(int maxFrameRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxFrameRate, 0);
        _interval = TimeSpan.FromSeconds(1.0 / maxFrameRate);
        _tolerance = _interval / 4;
    }

    /// <summary>Whether the picture taken at <paramref name="timestamp"/> is to be sent. Call once per picture, in capture order.</summary>
    public bool ShouldKeep(TimeSpan timestamp)
    {
        var previous = _lastSeen;
        _lastSeen = timestamp;

        // A timestamp that does not move forward (the clock restarted, or a source that stamps nothing) cannot be
        // paced by: keep the picture and start counting again from it, rather than dropping everything.
        if (previous is null || timestamp <= previous)
        {
            _nextDue = timestamp + _interval;
            return true;
        }

        if (timestamp < _nextDue - _tolerance)
        {
            return false;
        }

        // Stay on the grid, so a picture that arrived a little late does not cost the slot after it; but when the
        // input stalled for a whole slot or more, start the grid over from here instead of keeping a burst's worth
        // of slots open.
        var next = _nextDue + _interval;
        _nextDue = next < timestamp ? timestamp + _interval : next;
        return true;
    }
}
