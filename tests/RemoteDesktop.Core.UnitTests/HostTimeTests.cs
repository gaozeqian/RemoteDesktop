using RemoteDesktop.Platform.Shared;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>The host-clock conversion the two macOS frame sinks used to carry separately.</summary>
public sealed class HostTimeTests
{
    [Fact]
    public void Zero_is_no_stamp_rather_than_a_date()
    {
        // The helpers use zero for "this buffer had no presentation time"; treating it as a real timestamp would
        // make the frame look decades old and skew anything that compares it with a screen frame.
        Assert.Equal(TimeSpan.Zero, HostTime.ToTimestamp(0));
    }

    [Fact]
    public void Nanoseconds_become_ticks_at_one_hundred_nanoseconds_per_tick()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), HostTime.ToTimestamp(1_000_000_000));
        Assert.Equal(TimeSpan.FromTicks(123_456), HostTime.ToTimestamp(12_345_678));
    }
}
