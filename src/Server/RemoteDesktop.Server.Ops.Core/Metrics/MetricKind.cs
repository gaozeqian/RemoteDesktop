namespace RemoteDesktop.Server.Ops.Core;

/// <summary>
/// What a metric's number means, and therefore how it may be aggregated.
/// </summary>
/// <remarks>
/// The distinction is not bookkeeping. Averaging a counter is meaningless, and a counter that resets (a process restart)
/// reads as a negative delta — drawn as a traffic spike downward, then a spike up. The type comes from the exposition's
/// own <c># TYPE</c> line rather than from a configuration list, because a list is wrong the first time somebody adds a
/// metric.
/// </remarks>
public enum MetricKind
{
    /// <summary>A level: sum, average, min and max are all meaningful.</summary>
    Gauge,

    /// <summary>A monotonic count: only deltas over time mean anything.</summary>
    Counter,
}

/// <summary>Reads the exposition's type names.</summary>
public static class MetricKinds
{
    private const string Counter = "counter";
    private const string Gauge = "gauge";

    /// <summary>
    /// The kind named by a <c># TYPE</c> line, or <c>null</c> when it is missing or names something this console does
    /// not aggregate (histograms and summaries are not scraped in v1). Silence is deliberate: guessing a type is how a
    /// counter ends up averaged.
    /// </summary>
    public static MetricKind? FromPrometheusType(string? type)
    {
        if (string.Equals(type, Counter, StringComparison.OrdinalIgnoreCase))
        {
            return MetricKind.Counter;
        }

        return string.Equals(type, Gauge, StringComparison.OrdinalIgnoreCase) ? MetricKind.Gauge : null;
    }

    /// <summary>The exposition's spelling of <paramref name="kind"/>, for the catalog row.</summary>
    public static string ToPrometheusType(MetricKind kind) => kind == MetricKind.Counter ? Counter : Gauge;
}
