namespace RemoteDesktop.Server.Ops.Core;

/// <summary>
/// A downsampled aggregate of metric samples for one (node, metric, step, bucket).
/// </summary>
public sealed record MetricRollup(
    string NodeId,
    string Metric,
    int StepSeconds,
    long Bucket,
    double FirstValue,
    double LastValue,
    double MinValue,
    double MaxValue,
    double AvgValue,
    int SampleCount);
