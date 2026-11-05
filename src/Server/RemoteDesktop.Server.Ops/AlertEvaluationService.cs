using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops;

/// <summary>
/// Gives a new console one rule worth having. Without it the first visit to the alert page is an empty table and a
/// form, which reads as "nothing is wrong" rather than "nothing is watched".
/// </summary>
public sealed partial class AlertRuleSeeder(OpsStore store, TimeProvider time, ILogger<AlertRuleSeeder> logger) : IHostedService
{
    /// <summary>Inserts the default rule when the table is empty.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (await store.CountAlertRulesAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
            return;
        }

        await store.UpsertAlertRuleAsync(
            new AlertRule(
                Guid.NewGuid().ToString("N"),
                "节点抓取失败",
                AlertRuleKinds.Scrape,
                Metric: null,
                Numerator: null,
                Denominator: null,
                Aggregation: "avg",
                Comparator: AlertComparators.GreaterThan,
                Threshold: 0.5,
                WindowSeconds: 60,
                ForSeconds: 30,
                Severity: "P1",
                Enabled: true),
            cancellationToken).ConfigureAwait(false);

        await store.AppendAuditAsync(
            new OpsAuditEntry(
                Guid.NewGuid().ToString("N"),
                time.GetUtcNow().ToUnixTimeMilliseconds(),
                "system",
                "alert.rule.seed",
                "节点抓取失败",
                "{}",
                "-",
                "success"),
            cancellationToken).ConfigureAwait(false);

        Seeded(logger);
    }

    /// <summary>Nothing to stop.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded the default alert rule: a node whose scrape fails.")]
    private static partial void Seeded(ILogger logger);
}

/// <summary>Runs <see cref="AlertService"/> on a timer.</summary>
public sealed partial class AlertEvaluationService(
    AlertService alerts,
    OpsOptions options,
    OpsLease lease,
    TimeProvider time,
    ILogger<AlertEvaluationService> logger) : BackgroundService
{
    private const string LeaseName = "alerts";

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

    private TimeSpan EvaluationInterval => TimeSpan.FromSeconds(Math.Max(5, options.AlertIntervalSeconds));

    private async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(EvaluationInterval, time);

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
                // A database hiccup or one bad rule must not fault the worker: the default host behaviour for an
                // unhandled BackgroundService exception is to stop the console, and a monitoring console that goes
                // down when the thing it monitors misbehaves is worse than no console.
                EvaluationFailed(logger, ex);
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    /// <summary>One pass, public so a test can drive it without waiting for the timer.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!await lease.TryAcquireAsync(LeaseName, OpsLease.DurationFor(EvaluationInterval), cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var (opened, resolved) = await alerts.EvaluateOnceAsync(cancellationToken).ConfigureAwait(false);
        if (opened > 0 || resolved > 0)
        {
            Evaluated(logger, opened, resolved);
        }
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Alert evaluation opened {Opened} and resolved {Resolved} alerts.")]
    private static partial void Evaluated(ILogger logger, int opened, int resolved);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not release the alert evaluation lease; a restarted console may wait for it to expire.")]
    private static partial void LeaseReleaseFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Alert evaluation failed; will try again next pass.")]
    private static partial void EvaluationFailed(ILogger logger, Exception exception);
}
