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
    ILogger<ActionRecoveryService> logger)
    : LeasedPeriodicService(lease, "actions", time, logger)
{
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(30);

    protected override TimeSpan Interval => RecoveryInterval;

    protected override Task RunPassAsync(CancellationToken cancellationToken) => RunOnceAsync(cancellationToken);

    /// <summary>One pass, public so a test can drive it without waiting for the timer.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!await Lease.TryAcquireAsync(LeaseName, LeaseDuration, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        var now = Time.GetUtcNow();
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Marked stale action {Action} ({Kind}) as failed; it had no outcome after approval.")]
    private static partial void ActionRecovered(ILogger logger, string action, string kind);
}
