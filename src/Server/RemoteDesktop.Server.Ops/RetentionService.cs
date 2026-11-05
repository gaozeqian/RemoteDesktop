using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops;

/// <summary>
/// Deletes history the console no longer needs: old metric samples, events, projected sessions and resolved alerts.
/// </summary>
/// <remarks>
/// Without this the SQLite file grows for the life of the deployment — a scrape every ten seconds is millions of rows a
/// month — and a full disk turns into a database error that looks like every other failure. The deletes are chunked so
/// a page load still gets the database between batches, and a failure in one table does not stop the others.
/// Expired administrator sessions are removed on every pass regardless of the retention settings: they are not history,
/// they are rows that can no longer authenticate anything.
/// </remarks>
public sealed partial class RetentionService(
    OpsStore store,
    OpsOptions options,
    OpsLease lease,
    TimeProvider time,
    ILogger<RetentionService> logger) : BackgroundService
{
    private const string LeaseName = "retention";
    private const int BatchSize = 5000;
    private const int MaxBatchesPerTable = 20;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            // Give the lease up so a restarted console does not sit idle until the old one expires. The token is
            // already cancelled here, and a failed release only costs that wait.
            try
            {
                await lease.ReleaseAsync(LeaseName, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LeaseReleaseFailed(logger, ex);
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(options.Retention.IntervalMinutes);
        using var timer = new PeriodicTimer(interval, time);

        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                RetentionFailed(logger, ex);
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    /// <summary>One pass, public so a test can drive it without waiting for the timer.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        // The lease lasts longer than the interval so a pass that runs a little long does not hand the work to a
        // second instance in the middle of a delete.
        if (!await lease.TryAcquireAsync(LeaseName, TimeSpan.FromMinutes(options.Retention.IntervalMinutes + 5), cancellationToken)
                .ConfigureAwait(false))
        {
            RetentionSkipped(logger);
            return;
        }

        var now = time.GetUtcNow();
        var deletedSessions = await store.DeleteExpiredSessionsAsync(now.ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false);

        var samples = await DeleteInBatchesAsync(
            options.Retention.SampleDays,
            cutoff => store.DeleteSamplesBeforeAsync(cutoff, BatchSize, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        var events = await DeleteInBatchesAsync(
            options.Retention.EventDays,
            cutoff => store.DeleteEventsBeforeAsync(cutoff, BatchSize, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        var sessions = await DeleteInBatchesAsync(
            options.Retention.SessionDays,
            cutoff => store.DeleteSessionsBeforeAsync(cutoff, BatchSize, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        var alerts = await DeleteInBatchesAsync(
            options.Retention.AlertDays,
            cutoff => store.DeleteResolvedAlertsBeforeAsync(cutoff, BatchSize, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (deletedSessions + samples + events + sessions + alerts > 0)
        {
            RetentionCompleted(logger, deletedSessions, samples, events, sessions, alerts);
        }
    }

    private async Task<int> DeleteInBatchesAsync(
        int days,
        Func<long, Task<int>> deleteBatch,
        CancellationToken cancellationToken)
    {
        if (days <= 0)
        {
            return 0;
        }

        var cutoff = time.GetUtcNow().AddDays(-days).ToUnixTimeMilliseconds();
        var total = 0;

        // A single unbounded DELETE would hold the SQLite write lock for as long as the whole backlog takes. The
        // batch method returns fewer than the batch size when the backlog is gone.
        for (var batch = 0; batch < MaxBatchesPerTable; batch++)
        {
            var deleted = await deleteBatch(cutoff).ConfigureAwait(false);
            total += deleted;
            if (deleted < BatchSize)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        return total;
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Retention pass skipped; another instance holds the lease.")]
    private static partial void RetentionSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Retention removed {Sessions} expired admin sessions, {Samples} samples, {Events} events, {ProjectedSessions} sessions and {Alerts} alerts.")]
    private static partial void RetentionCompleted(
        ILogger logger,
        int sessions,
        int samples,
        int events,
        int projectedSessions,
        int alerts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not release the retention lease; a restarted console may wait for it to expire.")]
    private static partial void LeaseReleaseFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retention pass failed; the next pass will try again.")]
    private static partial void RetentionFailed(ILogger logger, Exception exception);
}
