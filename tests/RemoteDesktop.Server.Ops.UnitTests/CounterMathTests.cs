using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.UnitTests;

/// <summary>
/// Rates and resets. A counter that restarts is the case that turns a quiet dashboard into a false incident report,
/// so it is tested first and named loudly.
/// </summary>
public class CounterMathTests
{
    [Fact]
    public void Increase_is_the_sum_of_the_step_deltas()
    {
        CounterPoint[] points = [new(0, 100), new(1000, 160), new(2000, 260)];

        Assert.Equal(160, CounterMath.Increase(points));
        Assert.Equal(80, CounterMath.AverageRate(points));
        // The first interval carried 60 of the 160 over one second.
        Assert.Equal(60, CounterMath.Rates(points)[0].Value, 3);
    }

    [Fact]
    public void A_counter_that_restarts_does_not_read_as_negative_traffic()
    {
        CounterPoint[] points = [new(0, 1000), new(1000, 50), new(2000, 150)];

        // The drop to 50 is a restart: 50 units arrived since. Then 100 more. Total 150, never negative.
        Assert.Equal(150, CounterMath.Increase(points));
        Assert.Equal(75, CounterMath.AverageRate(points), 3);
    }

    [Fact]
    public void Rates_skip_readings_that_share_a_timestamp()
    {
        CounterPoint[] points = [new(0, 10), new(0, 20), new(1000, 30)];

        var rates = CounterMath.Rates(points);

        var rate = Assert.Single(rates);
        Assert.Equal(1000, rate.Timestamp);
        // The zero-length pair is skipped; the next one is measured from the later of the two readings at t=0.
        Assert.Equal(10, rate.Value, 3);
    }

    [Fact]
    public void One_reading_has_no_rate_rather_than_a_zero()
    {
        CounterPoint[] points = [new(0, 10)];

        Assert.Equal(0, CounterMath.AverageRate(points));
        Assert.Empty(CounterMath.Rates(points));
    }
}
