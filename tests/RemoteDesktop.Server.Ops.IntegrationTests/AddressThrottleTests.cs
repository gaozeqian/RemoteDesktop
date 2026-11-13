using RemoteDesktop.Server.Ops.Auth;
using RemoteDesktop.TestSupport;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// The shared per-address failure budget (docs/ops/README.md): fifty failures from one address, whatever names were
/// tried, lock that address for ten minutes.
/// </summary>
public class AddressThrottleTests
{
    [Fact]
    public void An_address_locks_on_the_fiftieth_failure_and_not_before()
    {
        var time = new TestClock(DateTimeOffset.UtcNow);
        var throttle = new AddressThrottle(time);

        for (var attempt = 0; attempt < 49; attempt++)
        {
            throttle.RecordFailure("198.51.100.7");
        }

        Assert.False(throttle.IsLocked("198.51.100.7", out _));

        throttle.RecordFailure("198.51.100.7");

        Assert.True(throttle.IsLocked("198.51.100.7", out var retryAfter));
        Assert.InRange(retryAfter.TotalSeconds, 599, 600);
    }

    [Fact]
    public void A_locked_address_does_not_lock_another_and_opens_after_ten_minutes()
    {
        var time = new TestClock(DateTimeOffset.UtcNow);
        var throttle = new AddressThrottle(time);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            throttle.RecordFailure("198.51.100.7");
        }

        Assert.True(throttle.IsLocked("198.51.100.7", out _));
        Assert.False(throttle.IsLocked("198.51.100.8", out _));

        time.Advance(TimeSpan.FromSeconds(601));

        Assert.False(throttle.IsLocked("198.51.100.7", out _));
    }
}
