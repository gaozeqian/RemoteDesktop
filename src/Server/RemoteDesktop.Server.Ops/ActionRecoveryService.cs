using System.Text.Json;
using RemoteDesktop.Server.Ops.Storage;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops;

/// <summary>
/// Closes management actions that were approved but never reached a terminal state.
/// </summary>
/// <remarks>
/// Approval writes <c>approved</c>, then calls the node, then writes <c>executed</c> or <c>failed</c>. A process that
/// dies between the first and last step leaves a row that <see cref="OpsStore.ApproveActionAsync"/> will not accept
/// again (it only approves a pending row) and that no request will ever complete. The console cannot know whether the
/// node acted, so it says so and marks the action failed; the operator can request it again, which is the safe
/// direction for an emergency revoke.
/// </remarks>
public sealed partial class ActionRecoveryService(
    OpsStore store,
    OpsOptions options,
    OpsLease lease,
    TimeProvider time,
    ILogger<ActionRecoveryService> logger) : BackgroundService
{
    private const string LeaseName = "actions";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

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
        using var timer = new PeriodicTimer(Interval, time);

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
                RecoveryFailed(logger, ex);
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    /// <summary>One pass, public so a test can drive it without waiting for the timer.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!await lease.TryAcquireAsync(LeaseName, OpsLease.DurationFor(Interval), cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        var now = time.GetUtcNow();
        var cutoff = now.AddSeconds(-options.ActionStaleSeconds).ToUnixTimeMilliseconds();
        var stale = await store.ListStaleApprovedActionsAsync(cutoff, cancellationToken: cancellationToken).ConfigureAwait(false);
        var recovered = 0;

        foreach (var action in stale)
        {
            if (!await store.CompleteActionAsync(
                    action.Id,
                    ActionStates.Failed,
                    now.ToUnixTimeMilliseconds(),
                    "the console restarted before the node answered; request the action again",
                    cancellationToken).ConfigureAwait(false))
            {
                // The handler finished between the query and this update.
                continue;
            }

            await store.AppendAuditAsync(
                new OpsAuditEntry(
                    Guid.NewGuid().ToString("N"),
                    now.ToUnixTimeMilliseconds(),
                    "system",
                    "action.recover",
                    action.Id,
                    JsonSerializer.Serialize(new { detail = $"approved {action.Kind} on device {action.DeviceId} was left without an outcome" }, OpsEventJson.Options),
                    "-",
                    "success"),
                cancellationToken).ConfigureAwait(false);

            recovered++;
            ActionRecovered(logger, action.Id, action.Kind);
        }

        return recovered;
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Marked stale action {Action} ({Kind}) as failed; it had no outcome after approval.")]
    private static partial void ActionRecovered(ILogger logger, string action, string kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not release the action recovery lease; a restarted console may wait for it to expire.")]
    private static partial void LeaseReleaseFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Action recovery pass failed; the next pass will try again.")]
    private static partial void RecoveryFailed(ILogger logger, Exception exception);
}
