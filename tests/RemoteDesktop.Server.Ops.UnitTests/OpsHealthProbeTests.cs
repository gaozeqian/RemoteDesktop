using System.Data.Common;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.TestSupport;

namespace RemoteDesktop.Server.Ops.UnitTests;

public sealed class OpsHealthProbeTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Checks_within_the_cache_window_share_one_database_probe()
    {
        var clock = new TestClock(Start);
        var probes = 0;
        using var probe = new OpsHealthProbe(
            _ =>
            {
                probes++;
                return Task.CompletedTask;
            },
            clock);

        Assert.True(await probe.IsHealthyAsync(Ct));
        Assert.True(await probe.IsHealthyAsync(Ct));
        Assert.Equal(1, probes);

        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.True(await probe.IsHealthyAsync(Ct));
        Assert.Equal(2, probes);
    }

    [Fact]
    public async Task An_unreachable_database_is_cached_like_a_healthy_one()
    {
        var clock = new TestClock(Start);
        var probes = 0;
        using var probe = new OpsHealthProbe(
            _ =>
            {
                probes++;
                throw new UnreachableException();
            },
            clock);

        Assert.False(await probe.IsHealthyAsync(Ct));
        Assert.False(await probe.IsHealthyAsync(Ct));
        Assert.Equal(1, probes);

        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(await probe.IsHealthyAsync(Ct));
        Assert.Equal(2, probes);
    }

    [Fact]
    public async Task Recovery_is_reported_after_the_cache_expires()
    {
        var clock = new TestClock(Start);
        var reachable = false;
        using var probe = new OpsHealthProbe(
            _ => reachable ? Task.CompletedTask : throw new UnreachableException(),
            clock);

        Assert.False(await probe.IsHealthyAsync(Ct));

        reachable = true;
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.True(await probe.IsHealthyAsync(Ct));
    }

    private sealed class UnreachableException : DbException
    {
    }
}
