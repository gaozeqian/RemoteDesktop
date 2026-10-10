namespace RemoteDesktop.Core.UnitTests;

/// <summary>A clock the test moves by hand, for everything that is counted in time: windows, expiries, rotation.</summary>
public sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
