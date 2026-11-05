using RemoteDesktop.Features.Camera;

namespace RemoteDesktop.Transport.IntegrationTests;

public sealed class FrameRateLimiterTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private static List<int> Kept(int maxFrameRate, IEnumerable<TimeSpan> timestamps)
    {
        var limiter = new FrameRateLimiter(maxFrameRate);
        return [.. timestamps.Select((t, i) => (t, i)).Where(x => limiter.ShouldKeep(x.t)).Select(x => x.i)];
    }

    [Fact]
    public void Sixty_fps_becomes_every_other_picture_and_the_first_is_kept()
    {
        var kept = Kept(30, Enumerable.Range(0, 60).Select(i => TimeSpan.FromSeconds(i / 60.0)));

        Assert.Equal(Enumerable.Range(0, 30).Select(i => i * 2), kept);
    }

    [Fact]
    public void Uneven_sixty_fps_input_is_not_over_dropped()
    {
        // Pictures jitter by up to 6 ms around the 16.7 ms grid; about 30 per second must still get through, and
        // never a gap longer than two input intervals plus the jitter.
        var random = new Random(1234);
        var times = Enumerable.Range(1, 600).Select(i => Ms((i * 1000.0 / 60) + (random.NextDouble() * 12) - 6)).ToArray();

        var kept = Kept(30, times);

        Assert.InRange(kept.Count, 280, 320); // 10 s of input
        Assert.All(kept.Zip(kept.Skip(1)), pair => Assert.True(times[pair.Second] - times[pair.First] < Ms(60)));
    }

    [Fact]
    public void Input_at_or_below_the_limit_is_untouched()
    {
        Assert.Equal(Enumerable.Range(0, 30), Kept(30, Enumerable.Range(0, 30).Select(i => TimeSpan.FromSeconds(i / 30.0))));
        Assert.Equal(Enumerable.Range(0, 25), Kept(30, Enumerable.Range(0, 25).Select(i => TimeSpan.FromSeconds(i / 25.0))));
        // 29.97 fps with timestamp noise
        Assert.Equal(Enumerable.Range(0, 20), Kept(30, Enumerable.Range(0, 20).Select(i => Ms((i * 33.37) + (i % 2 == 0 ? -3 : 3)))));
    }

    [Fact]
    public void A_rate_between_the_limit_and_double_still_averages_the_limit()
    {
        // 50 fps in, 30 out on average: the grid catches up, it does not fall to 25 fps.
        var kept = Kept(30, Enumerable.Range(0, 500).Select(i => TimeSpan.FromSeconds(i / 50.0)));

        Assert.InRange(kept.Count, 290, 310); // 10 s of input
    }

    [Fact]
    public void A_gap_in_the_input_is_not_made_up_with_a_burst()
    {
        // 60 fps, a one second stall, then 60 fps again.
        var times = Enumerable.Range(0, 10).Select(i => TimeSpan.FromSeconds(i / 60.0))
            .Concat(Enumerable.Range(0, 20).Select(i => TimeSpan.FromSeconds(2 + (i / 60.0)))).ToArray();

        var kept = Kept(30, times);

        // After the gap the first picture is kept, then every other one.
        Assert.Equal([0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28], kept);
    }

    [Fact]
    public void A_timestamp_that_does_not_advance_keeps_the_picture_instead_of_dropping_everything()
    {
        // A source that stamps nothing (all zero) must not end up with a stream of one picture.
        Assert.Equal([0, 1, 2, 3], Kept(30, [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]));

        // A clock that restarts: the picture after the restart is kept and pacing resumes from it.
        Assert.Equal([0, 2, 3, 5], Kept(30, [Ms(10_000), Ms(10_016.7), Ms(10_033.3), Ms(5), Ms(21.7), Ms(38.3)]));
    }

    [Fact]
    public void A_limit_that_is_not_positive_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRateLimiter(0));
    }
}
