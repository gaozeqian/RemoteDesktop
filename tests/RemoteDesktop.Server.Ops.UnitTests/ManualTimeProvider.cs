namespace RemoteDesktop.Server.Ops.UnitTests;

/// <summary>A clock the test moves by hand. Lockouts are about elapsed time, and sleeping for it would make the suite slow.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
