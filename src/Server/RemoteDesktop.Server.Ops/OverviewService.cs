using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops;

/// <summary>What the overview screen shows, with every number traceable to a series.</summary>
/// <param name="GeneratedAt">When this was assembled.</param>
/// <param name="Kpis">A null KPI means "no fresh reading", which the page draws as an em dash.</param>
/// <param name="WindowMinutes">How much traffic history the chart covers.</param>
/// <param name="Traffic">Bytes per second, bucketed; empty when nothing has been scraped yet.</param>
/// <param name="Nodes">The registered nodes and how their last scrape went.</param>
public sealed record Overview(
    DateTimeOffset GeneratedAt,
    OverviewKpis Kpis,
    int WindowMinutes,
    IReadOnlyList<TrafficPoint> Traffic,
    IReadOnlyList<OverviewNode> Nodes,
    IReadOnlyList<PlatformCount> Platforms,
    int DirectSessions,
    int RelayedSessions);

/// <summary>The four numbers at the top. The metric-derived ones are null without a fresh reading; the alert count is always a number.</summary>
public sealed record OverviewKpis(
    double? DevicesOnline,
    double? RelaySessions,
    double? EgressBytesPerSecond,
    double? AlertsToday);

/// <summary>One point of the traffic chart. Both directions are bytes per second at that bucket.</summary>
public sealed record TrafficPoint(long Timestamp, double ToHost, double ToController);

/// <summary>How many sessions of a window ran on one platform, as a client reported it.</summary>
public sealed record PlatformCount(string Platform, int Sessions);

/// <summary>A node as the overview lists it.</summary>
public sealed record OverviewNode(
    string Id,
    string Name,
    string Role,
    bool Enabled,
    long? LastScrapeAt,
    bool? LastScrapeOk);

/// <summary>
/// Assembles the overview from stored readings.
/// </summary>
/// <remarks>
/// The freshness cutoffs are the point of this type. A gauge older than <see cref="LatestWindow"/> is treated as
/// missing, not as the current value: showing a number from ten minutes ago under "online devices" is exactly the
/// polite lie the console's design forbids.
/// </remarks>
public sealed class OverviewService(OpsStore store, TimeProvider time)
{
    private static readonly TimeSpan LatestWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(2);

    private const int TrafficWindowMinutes = 30;
    private const long BucketMilliseconds = 30_000;

    /// <summary>How far back the link and platform split looks. A day keeps a quiet morning from looking like a trend.</summary>
    private const int MixWindowHours = 24;

    /// <summary>Builds the overview as of now.</summary>
    public async Task<Overview> BuildAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var nowUnix = now.ToUnixTimeMilliseconds();

        string[] gauges = [OpsMetricNames.DevicesOnline, OpsMetricNames.RelayPairingsActive];
        var latest = await store
            .ReadLatestAsync(gauges, nowUnix - (long)LatestWindow.TotalMilliseconds, cancellationToken)
            .ConfigureAwait(false);

        var devicesOnline = Sum(latest, OpsMetricNames.DevicesOnline);
        var relaySessions = Sum(latest, OpsMetricNames.RelayPairingsActive);

        string[] counters = [OpsMetricNames.RelayBytesToHost, OpsMetricNames.RelayBytesToController];
        var recent = await store
            .ReadSeriesAsync(counters, nowUnix - (long)RateWindow.TotalMilliseconds, nowUnix, cancellationToken)
            .ConfigureAwait(false);

        var egress = recent.Count == 0
            ? (double?)null
            : recent
                .GroupBy(point => (point.NodeId, point.Metric))
                .Sum(group => CounterMath.AverageRate(Points(group)));

        var trailing = await store
            .ReadSeriesAsync(counters, nowUnix - TrafficWindowMinutes * 60_000L, nowUnix, cancellationToken)
            .ConfigureAwait(false);

        // "Today" is the UTC day. The console has no per-deployment timezone setting, and pretending to know one would
        // make the number mean something different on every machine that reads it.
        var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var alertsToday = await store.CountAlertsSinceAsync(todayStart, cancellationToken).ConfigureAwait(false);

        var mix = await store
            .SummarizeSessionsAsync(nowUnix - MixWindowHours * 3_600_000L, cancellationToken)
            .ConfigureAwait(false);

        return new Overview(
            now,
            new OverviewKpis(devicesOnline, relaySessions, egress, alertsToday),
            TrafficWindowMinutes,
            BuildTraffic(trailing),
            await ListNodesAsync(cancellationToken).ConfigureAwait(false),
            [.. mix.Platforms.Select(pair => new PlatformCount(pair.Key, pair.Value))],
            mix.Direct,
            mix.Relayed);
    }

    private async Task<IReadOnlyList<OverviewNode>> ListNodesAsync(CancellationToken cancellationToken) =>
        (await store.ListNodesAsync(cancellationToken).ConfigureAwait(false))
        .Select(node => new OverviewNode(node.Id, node.Name, node.Role, node.Enabled, node.LastScrapeAt, node.LastScrapeOk))
        .ToArray();

    /// <summary>
    /// Turns per-node counter series into one rate series: rates inside a bucket are averaged (they are already per
    /// second), then summed across nodes. Summing the raw samples instead would multiply by the scrape rate.
    /// </summary>
    private static IReadOnlyList<TrafficPoint> BuildTraffic(IReadOnlyList<MetricPoint> points)
    {
        if (points.Count == 0)
        {
            return [];
        }

        var buckets = new Dictionary<long, Bucket>();

        foreach (var group in points.GroupBy(point => (point.NodeId, point.Metric)))
        {
            var isToHost = string.Equals(group.Key.Metric, OpsMetricNames.RelayBytesToHost, StringComparison.Ordinal);
            foreach (var rate in CounterMath.Rates(Points(group)))
            {
                var bucket = rate.Timestamp - (rate.Timestamp % BucketMilliseconds);
                if (!buckets.TryGetValue(bucket, out var current))
                {
                    current = default;
                }

                buckets[bucket] = isToHost
                    ? current with { ToHost = current.ToHost + rate.Value, HostCount = current.HostCount + 1 }
                    : current with { ToController = current.ToController + rate.Value, ControllerCount = current.ControllerCount + 1 };
            }
        }

        return [.. buckets
            .OrderBy(pair => pair.Key)
            .Select(pair => new TrafficPoint(
                pair.Key,
                pair.Value.ToHost / Math.Max(1, pair.Value.HostCount),
                pair.Value.ToController / Math.Max(1, pair.Value.ControllerCount)))];
    }

    /// <summary>Per-direction sums and counts inside one bucket. Counted separately, or averaging one direction would
    /// divide it by the other direction's samples as well.</summary>
    private readonly record struct Bucket(double ToHost, int HostCount, double ToController, int ControllerCount);

    private static List<CounterPoint> Points(IEnumerable<MetricPoint> points) =>
        [.. points.OrderBy(point => point.Timestamp).Select(point => new CounterPoint(point.Timestamp, point.Value))];

    private static double? Sum(IReadOnlyList<MetricPoint> points, string metric)
    {
        var values = points.Where(point => string.Equals(point.Metric, metric, StringComparison.Ordinal)).ToArray();
        return values.Length == 0 ? null : values.Sum(point => point.Value);
    }
}
