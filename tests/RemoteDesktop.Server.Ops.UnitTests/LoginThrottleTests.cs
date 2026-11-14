using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.TestSupport;

namespace RemoteDesktop.Server.Ops.UnitTests;

/// <summary>A lockout that does not expire is a denial of service with a nice name; one that never engages is decoration.</summary>
public class LoginThrottleTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_key_locks_after_the_budget_and_opens_again_after_the_window()
    {
        var time = new TestClock(Start);
        var throttle = new LoginThrottle(time, maxFailures: 3, lockSeconds: 60);

        throttle.RecordFailure("key");
        throttle.RecordFailure("key");
        Assert.False(throttle.IsLocked("key", out _));

        throttle.RecordFailure("key");
        Assert.True(throttle.IsLocked("key", out var retryAfter));
        Assert.InRange(retryAfter.TotalSeconds, 59, 60);

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.False(throttle.IsLocked("key", out _));
    }

    [Fact]
    public void A_successful_sign_in_forgets_the_failures()
    {
        var time = new TestClock(Start);
        var throttle = new LoginThrottle(time, maxFailures: 2, lockSeconds: 60);

        throttle.RecordFailure("key");
        throttle.Reset("key");
        throttle.RecordFailure("key");

        Assert.False(throttle.IsLocked("key", out _));
    }

    [Fact]
    public void Keys_do_not_share_a_budget()
    {
        var time = new TestClock(Start);
        var throttle = new LoginThrottle(time, maxFailures: 1, lockSeconds: 60);

        throttle.RecordFailure(LoginThrottle.KeyFor("10.0.0.1", "alice"));

        Assert.True(throttle.IsLocked(LoginThrottle.KeyFor("10.0.0.1", "alice"), out _));
        Assert.False(throttle.IsLocked(LoginThrottle.KeyFor("10.0.0.2", "alice"), out _));
        Assert.False(throttle.IsLocked(LoginThrottle.KeyFor("10.0.0.1", "bob"), out _));
    }

    [Fact]
    public void A_full_table_does_not_lock_an_unknown_key()
    {
        // The old behaviour made every unknown key locked once the table was full. An unauthenticated attacker could
        // fill it with ten thousand names and stop the administrator from signing in.
        var time = new TestClock(Start);
        var throttle = new LoginThrottle(time, maxFailures: 2, lockSeconds: 60, maxTrackedKeys: 2);

        throttle.RecordFailure("a");
        throttle.RecordFailure("b");

        Assert.False(throttle.IsLocked("c", out _));
        throttle.RecordFailure("c");
        Assert.False(throttle.IsLocked("c", out _));
    }

    [Fact]
    public void Making_room_evicts_an_unlocked_key_before_a_locked_one()
    {
        var time = new TestClock(Start);
        var throttle = new LoginThrottle(time, maxFailures: 2, lockSeconds: 60, maxTrackedKeys: 2);

        throttle.RecordFailure("locked");
        throttle.RecordFailure("locked");        // this key is now locked
        throttle.RecordFailure("old-unlocked"); // one failure: an entry, but not locked
        throttle.RecordFailure("new");           // forces eviction

        Assert.True(throttle.IsLocked("locked", out _));
        Assert.False(throttle.IsLocked("old-unlocked", out _));
    }

    [Fact]
    public void Spelling_variants_of_a_name_share_the_accounts_budget()
    {
        // The account lookup trims and upper-cases; a throttle that did not would let an attacker spend five guesses
        // on each spelling of the same account.
        Assert.Equal(
            LoginThrottle.KeyFor("10.0.0.1", "alice"),
            LoginThrottle.KeyFor("10.0.0.1", "  ALICE\t"));
    }
}
