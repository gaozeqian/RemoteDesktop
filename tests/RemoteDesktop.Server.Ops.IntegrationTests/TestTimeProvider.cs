namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// The clock the console under test uses. Tests advance it before each TOTP sign-in so every code belongs to a new
/// time step; a single-use second factor would otherwise reject the second sign-in in the same thirty seconds.
/// </summary>
public sealed class TestTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    /// <summary>Moves the clock forward and returns the new time.</summary>
    public DateTimeOffset Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _now = _now.Add(by);
            return _now;
        }
    }
}
