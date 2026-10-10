namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>A clock the test moves by hand, for the limits that are counted in time.</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
