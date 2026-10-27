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
    OpsOptions options,
    IHttpClientFactory httpClientFactory,
    TimeProvider time,
    ILogger<AlertService> logger)
{
    /// <summary>Named client for the webhook, so a slow receiver cannot hold an evaluation open indefinitely.</summary>
    public const string ClientName = "ops-webhook";

    private const string NotificationChannel = "webhook";

    /// <summary>Runs one pass. Returns how many alerts were opened and how many resolved.</summary>
    public async Task<(int Opened, int Resolved)> EvaluateOnceAsync(CancellationToken cancellationToken)
    {
        var rules = await store.ListAlertRulesAsync(cancellationToken).ConfigureAwait(false);
        var nodes = await store.ListNodesAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var opened = 0;
        var resolved = 0;

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
                    await NotifyAsync(alert, rule.Name, rule.Severity, "opened", cancellationToken).ConfigureAwait(false);
                    opened++;
                }
                else if (decision == AlertDecision.Resolve && open is not null)
                {
                    if (await store.ResolveAlertAsync(open.Id, now.ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false))
                    {
                        await AuditAsync("alert.resolve", rule.Name, open.Id, now, cancellationToken).ConfigureAwait(false);
                        await NotifyAsync(open, rule.Name, rule.Severity, "resolved", cancellationToken).ConfigureAwait(false);
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
            await NotifyAsync(orphan, name, orphan.Severity, "resolved", cancellationToken).ConfigureAwait(false);
            resolved++;
        }

        // A webhook that was down when an alert opened used to lose that page permanently: the open alert no longer
        // produces a transition, and NotifyAsync is only called from transitions. Retry the deliveries that are still
        // owed, with a backoff, before the pass ends.
        await RetryNotificationsAsync(now, cancellationToken).ConfigureAwait(false);

        return (opened, resolved);
    }

    /// <summary>
    /// Re-delivers notifications whose first attempt failed and whose alert is still in the matching state.
    /// </summary>
    private async Task RetryNotificationsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<OpsPendingNotification> pending;
        try
        {
            pending = await store
                .ListPendingNotificationsAsync(NotificationChannel, NotificationBackoff.MaxAttempts, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RetryScanFailed(logger, ex);
            return;
        }

        foreach (var item in pending)
        {
            if (!NotificationBackoff.IsDue(item.LastAttemptAt, item.Attempts, now))
            {
                continue;
            }

            // The labels are not needed to notify and are not stored on the attempt row; the alert's dedupe key is
            // the only identity the receiver needs beyond the id.
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

            await NotifyAsync(alert, item.RuleName, item.Severity, item.Transition, cancellationToken).ConfigureAwait(false);
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
                    $"rule:{rule.Id}:node:{node.Name}",
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
        return [($"rule:{rule.Id}", firing, value)];
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
        return ($"rule:{rule.Id}", firing, value);
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

    private async Task NotifyAsync(
        OpsAlertRow alert,
        string ruleName,
        string severity,
        string transition,
        CancellationToken cancellationToken)
    {
        var url = options.Notifications.WebhookUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (await store.HasNotificationAttemptAsync(alert.Id, NotificationChannel, transition, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var title = transition == "opened"
            ? $"[{severity}] {ruleName}"
            : $"[已恢复] {ruleName}";

        // The URL is checked at start-up, but this method also runs from the retry pass and must not let a malformed
        // value thrown from Uri's constructor escape into the background loop.
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var webhook)
            || (webhook.Scheme != Uri.UriSchemeHttp && webhook.Scheme != Uri.UriSchemeHttps))
        {
            NotifyFailed(logger, ruleName, "Ops:Notifications:WebhookUrl is not an absolute http(s) URL");
            await TryRecordAttemptAsync(alert, transition, sent: false, "invalid webhook URL", now, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            using var client = httpClientFactory.CreateClient(ClientName);
            var payload = JsonSerializer.Serialize(
                new
                {
                    @event = transition,
                    title,
                    alert = new
                    {
                        id = alert.Id,
                        rule = ruleName,
                        severity,
                        value = alert.Value,
                        openedAt = alert.OpenedAt,
                        resolvedAt = transition == "resolved" ? alert.ResolvedAt ?? now : (long?)null,
                    },
                },
                OpsEventJson.Options);

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(webhook, content, cancellationToken).ConfigureAwait(false);

            var sent = response.IsSuccessStatusCode;
            await TryRecordAttemptAsync(
                alert,
                transition,
                sent,
                sent ? null : $"HTTP {(int)response.StatusCode}",
                now,
                cancellationToken).ConfigureAwait(false);

            if (!sent)
            {
                NotifyRejected(logger, ruleName, (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            await TryRecordAttemptAsync(alert, transition, sent: false, "timed out", now, cancellationToken).ConfigureAwait(false);
            NotifyFailed(logger, ruleName, "timed out");
        }
        catch (Exception ex)
        {
            // A DNS failure, a TLS error, a response the handler could not parse: none of these may reach the
            // evaluation loop, and all of them are worth another attempt later.
            await TryRecordAttemptAsync(alert, transition, sent: false, ex.Message, now, cancellationToken).ConfigureAwait(false);
            NotifyFailed(logger, ruleName, ex.Message);
        }
    }

    /// <summary>Records an attempt without letting a database failure escape the notifier.</summary>
    private async Task TryRecordAttemptAsync(
        OpsAlertRow alert,
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
                NotificationChannel,
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "The webhook for rule {Rule} answered HTTP {Status}.")]
    private static partial void NotifyRejected(ILogger logger, string rule, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The webhook for rule {Rule} failed: {Reason}.")]
    private static partial void NotifyFailed(ILogger logger, string rule, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record a webhook attempt; the retry pass may not see this alert.")]
    private static partial void NotifyRecordFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read pending webhook deliveries; will try again next pass.")]
    private static partial void RetryScanFailed(ILogger logger, Exception exception);
}
