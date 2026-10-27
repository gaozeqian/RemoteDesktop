using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.UnitTests;

/// <summary>A retry that runs on every evaluation is a load generator; one that never runs is a lost page.</summary>
public class NotificationBackoffTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_first_failure_waits_a_minute_and_the_delay_doubles()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), NotificationBackoff.Delay(1));
        Assert.Equal(TimeSpan.FromMinutes(2), NotificationBackoff.Delay(2));
        Assert.Equal(TimeSpan.FromMinutes(4), NotificationBackoff.Delay(3));
    }

    [Fact]
    public void The_delay_is_capped()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), NotificationBackoff.Delay(20));
    }

    [Fact]
    public void A_delivery_with_no_attempt_is_due()
    {
        Assert.True(NotificationBackoff.IsDue(null, 1, Start));
    }

    [Fact]
    public void A_delivery_is_not_due_before_its_backoff_and_is_due_after()
    {
        var last = Start.ToUnixTimeMilliseconds();

        Assert.False(NotificationBackoff.IsDue(last, 1, Start.AddSeconds(30)));
        Assert.True(NotificationBackoff.IsDue(last, 1, Start.AddSeconds(60)));
    }
}
