using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops;

/// <summary>
/// Periodically aggregates raw metric samples into 1-minute and 1-hour rollup buckets in <c>ops_metric_rollup</c>.
/// </summary>
public sealed partial class RollupMaintenanceService(
    OpsStore store,
    OpsLease lease,
    TimeProvider time,
    ILogger<RollupMaintenanceService> logger)
    : LeasedPeriodicService(lease, "rollup", time, logger)
{
    /// <summary>How often the rollup pass runs.</summary>
    public static readonly TimeSpan ServiceInterval = TimeSpan.FromMinutes(10);

    protected override TimeSpan Interval => ServiceInterval;

    protected override Task RunPassAsync(CancellationToken cancellationToken) => RunOnceAsync(cancellationToken);

    /// <summary>One pass, public so tests can drive it directly.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!await Lease.TryAcquireAsync(LeaseName, LeaseDuration, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var now = Time.GetUtcNow().ToUnixTimeMilliseconds();

        // 1. Rollup into 60-second (1-minute) buckets for samples up to 1 minute ago
        var minuteCutoff = now - 60_000;
        var minuteCount = await store.RollupSamplesAsync(minuteCutoff, 60, cancellationToken).ConfigureAwait(false);

        // 2. Rollup into 3600-second (1-hour) buckets for samples up to 1 hour ago
        var hourCutoff = now - 3_600_000;
        var hourCount = await store.RollupSamplesAsync(hourCutoff, 3600, cancellationToken).ConfigureAwait(false);

        if (minuteCount > 0 || hourCount > 0)
        {
            RollupCompleted(logger, minuteCount, hourCount);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Rollup pass complete: {MinuteBuckets} 1-minute buckets, {HourBuckets} 1-hour buckets.")]
    private static partial void RollupCompleted(ILogger logger, int minuteBuckets, int hourBuckets);
}
