using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

[Collection("ops-console")]
public class RollupTests(OpsHostFixture fixture) : IClassFixture<OpsHostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A pass continues from the newest bucket instead of re-aggregating the whole retention window. The first pass
    /// on an empty rollup table is bounded to one day; an older sample is not rewritten into history, and the next
    /// pass only revisits the newest bucket.
    /// </summary>
    [Fact]
    public async Task Rollup_continues_from_the_newest_bucket_instead_of_rebuilding_all_history()
    {
        var store = fixture.Services.GetRequiredService<OpsStore>();
        var nodeId = "node-rollup-window-" + Guid.NewGuid().ToString("N");
        var metric = "cpu_rollup_window";
        var now = fixture.Now.ToUnixTimeMilliseconds();
        var ancient = now - (long)TimeSpan.FromDays(10).TotalMilliseconds;
        var recent = now - 30_000;

        await store.WriteSamplesAsync(nodeId, ancient, [new PrometheusSample(metric, metric, 1.0)], Ct);
        await store.WriteSamplesAsync(nodeId, recent, [new PrometheusSample(metric, metric, 2.0)], Ct);

        var first = await store.RollupSamplesIncrementallyAsync(now, 60, Ct);
        Assert.True(first <= 2, $"The first pass rolled {first} buckets; it must be bounded, not the whole history.");

        var ancientBucket = ancient / 60_000;
        Assert.Empty(await store.GetMetricRollupsAsync(nodeId, metric, 60, ancientBucket, ancientBucket, Ct));

        // The next pass only re-rolls the newest bucket (and possibly one preceding it for late samples).
        var second = await store.RollupSamplesIncrementallyAsync(now, 60, Ct);
        Assert.True(second <= 2, $"The incremental pass rolled {second} buckets.");
    }

    /// <summary>
    /// A pass with no new samples must not rewrite the buckets it re-read: the incremental design exists because the
    /// full pass rewrote history every ten minutes. One sample in the current bucket is one changed row.
    /// </summary>
    [Fact]
    public async Task A_quiet_incremental_pass_writes_no_rows_and_one_new_sample_writes_one()
    {
        var store = fixture.Services.GetRequiredService<OpsStore>();
        var nodeId = "node-rollup-quiet-" + Guid.NewGuid().ToString("N");
        var metric = "cpu_rollup_quiet";
        var now = fixture.Now.ToUnixTimeMilliseconds();
        var bucketStart = now / 60_000 * 60_000;

        await store.WriteSamplesAsync(nodeId, bucketStart + 10_000, [new PrometheusSample(metric, metric, 1.0)], Ct);

        Assert.True(await store.RollupSamplesIncrementallyAsync(now, 60, Ct) > 0, "the first pass has to write the bucket");
        Assert.Equal(0, await store.RollupSamplesIncrementallyAsync(now, 60, Ct));

        await store.WriteSamplesAsync(nodeId, bucketStart + 20_000, [new PrometheusSample(metric, metric, 2.0)], Ct);
        Assert.Equal(1, await store.RollupSamplesIncrementallyAsync(now, 60, Ct));
    }

    [Fact]
    public async Task Rollup_aggregates_metric_samples_into_time_buckets()
    {
        var store = fixture.Services.GetRequiredService<OpsStore>();
        var nodeId = "node-rollup-test";
        var metric = "cpu_usage";

        // The start of the current minute, not a fixed epoch. A timestamp from 2023 is older than the 30-day sample
        // retention, and the host runs a retention pass when it starts — at the same moment this class's fixture is
        // seeding its accounts. That pass could delete the first samples between two writes here, and the bucket then
        // came back with 20 as its first value instead of 10. Aligned to the minute, the three samples are in one
        // bucket and inside every retention window; a bucket that ends at "now" is also one the background rollup
        // pass leaves alone, because that pass stops at now - 60s.
        var baseTime = fixture.Now.ToUnixTimeMilliseconds() / 60_000 * 60_000;

        List<PrometheusSample> samples1 = [new PrometheusSample(metric, metric, 10.0)];
        List<PrometheusSample> samples2 = [new PrometheusSample(metric, metric, 30.0)];
        List<PrometheusSample> samples3 = [new PrometheusSample(metric, metric, 20.0)];

        await store.WriteSamplesAsync(nodeId, baseTime + 10_000, samples1, Ct);
        await store.WriteSamplesAsync(nodeId, baseTime + 20_000, samples2, Ct);
        await store.WriteSamplesAsync(nodeId, baseTime + 30_000, samples3, Ct);

        // Rollup into 60-second bucket
        var stepSeconds = 60;
        var count = await store.RollupSamplesAsync(baseTime + 60_000, stepSeconds, Ct);
        Assert.True(count >= 1);

        var bucket = (baseTime + 10_000) / (stepSeconds * 1000);
        var rollups = await store.GetMetricRollupsAsync(nodeId, metric, stepSeconds, bucket, bucket, Ct);

        Assert.NotEmpty(rollups);
        var rollup = rollups[0];
        Assert.Equal(nodeId, rollup.NodeId);
        Assert.Equal(metric, rollup.Metric);
        Assert.Equal(stepSeconds, rollup.StepSeconds);
        Assert.Equal(bucket, rollup.Bucket);
        Assert.Equal(10.0, rollup.FirstValue);
        Assert.Equal(20.0, rollup.LastValue);
        Assert.Equal(10.0, rollup.MinValue);
        Assert.Equal(30.0, rollup.MaxValue);
        Assert.Equal(20.0, rollup.AvgValue);
        Assert.Equal(3, rollup.SampleCount);

        // A later pass replaces the bucket, so first stays put while last, extrema, average and count advance.
        await store.WriteSamplesAsync(nodeId, baseTime + 35_000, [new PrometheusSample(metric, metric, 40.0)], Ct);
        await store.RollupSamplesAsync(baseTime + 60_000, stepSeconds, Ct);
        rollup = Assert.Single(await store.GetMetricRollupsAsync(nodeId, metric, stepSeconds, bucket, bucket, Ct));
        Assert.Equal(10.0, rollup.FirstValue);
        Assert.Equal(40.0, rollup.LastValue);
        Assert.Equal(25.0, rollup.AvgValue);
        Assert.Equal(4, rollup.SampleCount);
    }
}
