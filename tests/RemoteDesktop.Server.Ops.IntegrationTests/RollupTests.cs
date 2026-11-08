using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

[Collection("ops-console")]
public class RollupTests(OpsHostFixture fixture) : IClassFixture<OpsHostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Rollup_aggregates_metric_samples_into_time_buckets()
    {
        var store = fixture.Services.GetRequiredService<OpsStore>();
        var nodeId = "node-rollup-test";
        var metric = "cpu_usage";
        var baseTime = 1_700_000_000_000L; // the samples below remain in one minute bucket

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
