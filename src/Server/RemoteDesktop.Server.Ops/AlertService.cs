using System.Text;
using System.Text.Json;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops;

/// <summary>
/// Evaluates every enabled rule, opens and resolves alerts, and tells the configured webhook.
/// </summary>
/// <remarks>
/// One evaluation is a pure read of the last few minutes of series; the only state it keeps between runs is the
/// "how long has this been true" timer, and the only state it trusts is the database's open alerts. That is what makes
/// a restart safe: nothing is re-alerted because the open row is still there.
/// </remarks>
public sealed partial class AlertService(
    OpsStore store,
    AlertStateMachine machine,
    IEnumerable<INotificationChannel> channels,
    TimeProvider time,
    ILogger<AlertService> logger)
{
    private readonly INotificationChannel[] _channels = [.. channels];

    /// <summary>
    /// Deliveries a single pass may retry per channel. Every send can wait out that channel's timeout, and a pass
    /// holding the alert lease must not be dragged on by a backlog: the rest stay owed and are due again next pass.
    /// </summary>
    private const int MaxRetriesPerPass = 2;

    /// <summary>Runs one pass. Returns how many alerts were opened and how many resolved.</summary>
    public async Task<(int Opened, int Resolved)> EvaluateOnceAsync(CancellationToken cancellationToken)
    {
        var rules = await store.ListAlertRulesAsync(cancellationToken).ConfigureAwait(false);
        var nodes = await store.ListNodesAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var opened = 0;
        var resolved = 0;

        // Read once per pass: a silence is a handful of rows, and a notification path that read the table per alert
        // would be a query per mail. A failure here must not stop alerts either, so it reads as "nothing silenced".
        var silences = await LoadSilencesAsync(now, cancellationToken).ConfigureAwait(false);

        // Every dedupe key this pass actually looked at. An open alert outside this set has no signal behind it at
        // all: its node was deleted, its rule was disabled, or the rule itself is gone. Comparing a current reading
        // with a threshold cannot reach those, so they would otherwise stay open forever.
        var observed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rule in rules.Where(rule => rule.Enabled))
        {
            foreach (var (dedupeKey, firing, value) in await SignalsAsync(rule, nodes, now, cancellationToken).ConfigureAwait(false))
            {
                observed.Add(dedupeKey);
                var open = await store.FindOpenAlertAsync(dedupeKey, cancellationToken).ConfigureAwait(false);
                var decision = machine.Observe(dedupeKey, firing, rule.ForSeconds, open is not null);

                if (decision == AlertDecision.Open)
                {
                    var alert = new OpsAlertRow(
                        Guid.NewGuid().ToString("N"),
                        rule.Id,
                        rule.Severity,
                        dedupeKey,
                        "open",
                        value,
                        "{}",
                        now.ToUnixTimeMilliseconds(),
                        null,
                        null,
                        null);

                    await store.OpenAlertAsync(alert, cancellationToken).ConfigureAwait(false);
                    await AuditAsync("alert.open", rule.Name, rule.Severity, now, cancellationToken).ConfigureAwait(false);
                    await NotifyAsync(alert, rule.Name, rule.Severity, "opened", silences, cancellationToken).ConfigureAwait(false);
                    opened++;
                }
                else if (decision == AlertDecision.Resolve && open is not null)
                {
                    if (await store.ResolveAlertAsync(open.Id, now.ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false))
                    {
                        await AuditAsync("alert.resolve", rule.Name, open.Id, now, cancellationToken).ConfigureAwait(false);
                        await NotifyAsync(open, rule.Name, rule.Severity, "resolved", silences, cancellationToken).ConfigureAwait(false);
                        resolved++;
                    }
                }
            }
        }

        var rulesById = rules.ToDictionary(rule => rule.Id, StringComparer.Ordinal);
        foreach (var orphan in await store.ListOpenAlertsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (observed.Contains(orphan.DedupeKey))
            {
                continue;
            }

            if (!await store.ResolveAlertAsync(orphan.Id, now.ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            // The rule may have been deleted along with the node that opened the alert; the dedupe key is the only
            // name left, and a resolved notification with an id in it is better than a permanent open one.
            var name = rulesById.TryGetValue(orphan.RuleId, out var rule) ? rule.Name : orphan.DedupeKey;
            await AuditAsync("alert.resolve", name, orphan.Id, now, cancellationToken).ConfigureAwait(false);
            await NotifyAsync(orphan, name, orphan.Severity, "resolved", silences, cancellationToken).ConfigureAwait(false);
            resolved++;
        }

        // A webhook that was down when an alert opened used to lose that page permanently: the open alert no longer
        // produces a transition, and NotifyAsync is only called from transitions. Retry the deliveries that are still
        // owed, with a backoff, before the pass ends.
        await RetryNotificationsAsync(now, silences, cancellationToken).ConfigureAwait(false);

        return (opened, resolved);
    }

    /// <summary>
    /// Re-delivers notifications whose first attempt failed and whose alert is still in the matching state.
    /// </summary>
    /// <summary>Reads the silences that still apply; a database failure reads as "nothing is silenced".</summary>
    private async Task<IReadOnlyList<SilenceWindow>> LoadSilencesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            var rows = await store
                .ListActiveSilencesAsync(now.ToUnixTimeMilliseconds(), cancellationToken)
                .ConfigureAwait(false);

            return [.. rows.Select(row => new SilenceWindow(row.RuleId, row.Node, row.UntilAt))];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SilenceScanFailed(logger, ex);
            return [];
        }
    }

    private async Task RetryNotificationsAsync(
        DateTimeOffset now,
        IReadOnlyList<SilenceWindow> silences,
        CancellationToken cancellationToken)
    {
        // Per channel: a webhook that has been down says nothing about the mail, and each keeps its own attempt count.
        foreach (var channel in _channels)
        {
            if (!channel.IsConfigured)
            {
                continue;
            }

            IReadOnlyList<OpsPendingNotification> pending;
            try
            {
                pending = await store
                    .ListPendingNotificationsAsync(channel.Id, NotificationBackoff.MaxAttempts, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                RetryScanFailed(logger, ex);
                continue;
            }

            var retried = 0;
            foreach (var item in pending)
            {
                if (!NotificationBackoff.IsDue(item.LastAttemptAt, item.Attempts, now))
                {
                    continue;
                }

                // A silence that was created after the first attempt still suppresses the retry: the row simply
                // stays owed until the window closes, and is dropped once the alert leaves the matching state.
                if (SilenceMatcher.IsSilenced(
                        silences,
                        item.RuleId,
                        AlertDedupeKeys.NodeOf(item.DedupeKey),
                        now.ToUnixTimeMilliseconds()))
                {
                    continue;
                }

                if (retried >= MaxRetriesPerPass)
                {
                    // The rest are still owed; the next pass picks them up in the same order.
                    break;
                }

                // The labels are not needed to notify and are not stored on the attempt row; the alert's dedupe key
                // is the only identity the receiver needs beyond the id.
                var alert = new OpsAlertRow(
                    item.AlertId,
                    item.RuleId,
                    item.Severity,
                    item.DedupeKey,
                    item.State,
                    item.Value,
                    "{}",
                    item.OpenedAt,
                    null,
                    null,
                    item.ResolvedAt);

                var error = await channel
                    .SendAsync(alert, item.RuleName, item.Severity, item.Transition, cancellationToken)
                    .ConfigureAwait(false);
                await TryRecordAttemptAsync(
                    alert,
                    channel.Id,
                    item.Transition,
                    error is null,
                    error,
                    now.ToUnixTimeMilliseconds(),
                    cancellationToken).ConfigureAwait(false);
                retried++;
            }
        }
    }

    /// <summary>
    /// The signals one rule watches: one per node for a scrape rule, one cluster-wide for a metric rule.
    /// </summary>
    private async Task<IReadOnlyList<(string Key, bool Firing, double? Value)>> SignalsAsync(
        AlertRule rule,
        IReadOnlyList<OpsNode> nodes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.Equals(rule.Kind, AlertRuleKinds.Scrape, StringComparison.Ordinal))
        {
            // Disabled nodes are included with "not firing" rather than dropped: dropping the signal would leave an
            // alert opened before the node was switched off open forever.
            return
            [
                .. nodes.Select(node => (
                    AlertDedupeKeys.ForNode(rule.Id, node.Name),
                    node.Enabled && node.LastScrapeOk == false,
                    (double?)(node.Enabled && node.LastScrapeOk == false ? 1 : 0))),
            ];
        }

        if (AlertRuleKinds.IsSessionQuality(rule.Kind))
        {
            return [await SessionQualityAsync(rule, now, cancellationToken).ConfigureAwait(false)];
        }

        var value = await MetricValueAsync(rule, now, cancellationToken).ConfigureAwait(false);
        var firing = value is { } current && AlertComparators.Evaluate(current, rule.Comparator, rule.Threshold);
        return [(AlertDedupeKeys.ForRule(rule.Id), firing, value)];
    }

    /// <summary>
    /// The two session-quality rules. They read the sessions clients reported on, not the metric table: RTT and link
    /// type exist only in a heartbeat, and a rule that silently read zero samples would look healthy forever.
    /// </summary>
    private async Task<(string Key, bool Firing, double? Value)> SessionQualityAsync(
        AlertRule rule,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var since = now.AddSeconds(-Math.Max(60, rule.WindowSeconds)).ToUnixTimeMilliseconds();

        double? value;
        if (string.Equals(rule.Kind, AlertRuleKinds.SessionRtt, StringComparison.Ordinal))
        {
            value = await store.AverageRttAsync(since, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var mix = await store.SummarizeSessionsAsync(since, cancellationToken).ConfigureAwait(false);
            var total = mix.Direct + mix.Relayed;
            value = total == 0 ? null : (double)mix.Direct / total;
        }

        var firing = value is { } current && AlertComparators.Evaluate(current, rule.Comparator, rule.Threshold);
        return (AlertDedupeKeys.ForRule(rule.Id), firing, value);
    }

    private async Task<double?> MetricValueAsync(AlertRule rule, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var from = now.AddSeconds(-Math.Max(30, rule.WindowSeconds)).ToUnixTimeMilliseconds();
        var to = now.ToUnixTimeMilliseconds();

        if (string.Equals(rule.Kind, AlertRuleKinds.Ratio, StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(rule.Numerator) || string.IsNullOrEmpty(rule.Denominator))
            {
                return null;
            }

            var numerator = await SumAsync(rule.Numerator, from, to, rule.Aggregation, cancellationToken).ConfigureAwait(false);
            var denominator = await SumAsync(rule.Denominator, from, to, rule.Aggregation, cancellationToken).ConfigureAwait(false);
            return numerator is { } top && denominator is { } bottom && bottom != 0 ? top / bottom : null;
        }

        return string.IsNullOrEmpty(rule.Metric)
            ? null
            : await SumAsync(rule.Metric, from, to, rule.Aggregation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One number for the rule: per-node aggregation, then summed across nodes. A counter is rated first — averaging a
    /// counter is meaningless, and the catalog is what says which it is.
    /// </summary>
    private async Task<double?> SumAsync(string metric, long from, long to, string aggregation, CancellationToken cancellationToken)
    {
        var points = await store.ReadSeriesAsync([metric], from, to, cancellationToken).ConfigureAwait(false);
        if (points.Count == 0)
        {
            return null;
        }

        var kind = await store.ReadMetricKindAsync(metric, cancellationToken).ConfigureAwait(false);

        var perNode = points
            .GroupBy(point => point.NodeId)
            .Select(group =>
            {
                var ordered = group.OrderBy(point => point.Timestamp).ToArray();
                if (string.Equals(kind, "counter", StringComparison.Ordinal))
                {
                    return CounterMath.AverageRate([.. ordered.Select(point => new CounterPoint(point.Timestamp, point.Value))]);
                }

                return aggregation switch
                {
                    "max" => ordered.Max(point => point.Value),
                    "min" => ordered.Min(point => point.Value),
                    _ => ordered.Average(point => point.Value),
                };
            })
            .ToArray();

        return perNode.Length == 0 ? null : perNode.Sum();
    }

    /// <summary>
    /// Offers the transition to every configured channel. Each one decides for itself and is recorded on its own row,
    /// so a webhook that is down neither blocks the mail nor causes it to be sent twice.
    /// </summary>
    private async Task NotifyAsync(
        OpsAlertRow alert,
        string ruleName,
        string severity,
        string transition,
        IReadOnlyList<SilenceWindow> silences,
        CancellationToken cancellationToken)
    {
        // Silence suppresses the pages, never the alert: the row was already opened (or resolved) and audited, and
        // the silence is not recorded as an attempt, so nothing is delivered after the window closes either.
        if (SilenceMatcher.IsSilenced(
                silences,
                alert.RuleId,
                AlertDedupeKeys.NodeOf(alert.DedupeKey),
                time.GetUtcNow().ToUnixTimeMilliseconds()))
        {
            LogSilenced(logger, alert.Id, ruleName);
            return;
        }

        foreach (var channel in _channels)
        {
            if (!channel.IsConfigured)
            {
                continue;
            }

            if (await store.HasNotificationAttemptAsync(alert.Id, channel.Id, transition, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var error = await channel
                .SendAsync(alert, ruleName, severity, transition, cancellationToken)
                .ConfigureAwait(false);

            await TryRecordAttemptAsync(
                alert,
                channel.Id,
                transition,
                error is null,
                error,
                time.GetUtcNow().ToUnixTimeMilliseconds(),
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Records an attempt without letting a database failure escape the notifier.</summary>
    private async Task TryRecordAttemptAsync(
        OpsAlertRow alert,
        string channelId,
        string transition,
        bool sent,
        string? error,
        long at,
        CancellationToken cancellationToken)
    {
        try
        {
            await store.RecordNotificationAttemptAsync(
                alert.Id,
                channelId,
                transition,
                sent,
                error,
                at,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            NotifyRecordFailed(logger, ex);
        }
    }

    private Task<string> AuditAsync(string action, string target, string detail, DateTimeOffset at, CancellationToken cancellationToken) =>
        store.AppendAuditAsync(
            new OpsAuditEntry(
                Guid.NewGuid().ToString("N"),
                at.ToUnixTimeMilliseconds(),
                "system",
                action,
                target,
                JsonSerializer.Serialize(new { detail }, OpsEventJson.Options),
                "-",
                "success"),
            cancellationToken);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record a notification attempt; the retry pass may not see this alert.")]
    private static partial void NotifyRecordFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read pending deliveries; will try again next pass.")]
    private static partial void RetryScanFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Alert {Alert} of rule {Rule} was silenced: the alert stands, no notification was sent.")]
    private static partial void LogSilenced(ILogger logger, string alert, string rule);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the silence windows; notifications are sent as if nothing were silenced.")]
    private static partial void SilenceScanFailed(ILogger logger, Exception exception);
}
