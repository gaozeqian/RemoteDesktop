namespace RemoteDesktop.TestSupport;

/// <summary>
/// A clock the test moves by hand. Every suite had grown its own copy (a different start, some with a lock, some
/// returning the new time); this is the one implementation, with the start still chosen by the test.
/// </summary>
internal sealed class TestClock : TimeProvider
{
    /// <summary>The start the unit suites assumed before this was shared, kept so their relative expectations hold.</summary>
    public static readonly DateTimeOffset DefaultStart = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly Lock _gate = new();
    private DateTimeOffset _now;

    /// <param name="start">The UTC time the clock reads before anything advances it.</param>
    public TestClock(DateTimeOffset? start = null) => _now = start ?? DefaultStart;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    /// <summary>Moves the clock forward and returns the new time; callers that only need the side effect ignore it.</summary>
    public DateTimeOffset Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by;
            return _now;
        }
    }
}
