using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops;

/// <summary>
/// The timer, the per-pass exception boundary and the lease release that the console's background passes all need.
/// </summary>
/// <remarks>
/// Four services had this loop copied word for word, and their lease durations had already drifted: retention used
/// interval + 5 minutes while the others used <see cref="OpsLease.DurationFor"/>. The pass itself stays in its own
/// service; only the loop is shared.
/// </remarks>
public abstract partial class LeasedPeriodicService(
    OpsLease lease,
    string leaseName,
    TimeProvider time,
    ILogger logger) : BackgroundService
{
    protected OpsLease Lease { get; } = lease;

    protected TimeProvider Time { get; } = time;

    protected ILogger Logger { get; } = logger;

    protected string LeaseName { get; } = leaseName;

    protected abstract TimeSpan Interval { get; }

    /// <summary>How long a pass may hold the lease. Longer than the interval, so a slow pass is not taken over.</summary>
    protected virtual TimeSpan LeaseDuration => OpsLease.DurationFor(Interval);

    /// <summary>One pass. The services keep their public <c>RunOnceAsync</c> so a test can drive it directly.</summary>
    protected abstract Task RunPassAsync(CancellationToken cancellationToken);

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
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
                await Lease.ReleaseAsync(LeaseName, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LeaseReleaseFailed(Logger, LeaseName, ex);
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, Time);

        do
        {
            try
            {
                await RunPassAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The default host behaviour for an unhandled BackgroundService exception is to stop the console. A
                // monitoring console that goes down when the thing it monitors misbehaves is worse than no console.
                PassFailed(Logger, LeaseName, ex);
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not release the {Service} lease; a restarted console may wait for it to expire.")]
    private static partial void LeaseReleaseFailed(ILogger logger, string service, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Service} pass failed; the next pass will try again.")]
    private static partial void PassFailed(ILogger logger, string service, Exception exception);
}
