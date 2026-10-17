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
        if (rules.Count == 0)
        {
            return (0, 0);
        }

        var nodes = await store.ListNodesAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var opened = 0;
        var resolved = 0;

        foreach (var rule in rules.Where(rule => rule.Enabled))
        {
            foreach (var (dedupeKey, firing, value) in await SignalsAsync(rule, nodes, now, cancellationToken).ConfigureAwait(false))
            {
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
                    await NotifyAsync(alert, rule, "opened", cancellationToken).ConfigureAwait(false);
                    opened++;
                }
                else if (decision == AlertDecision.Resolve && open is not null)
                {
                    if (await store.ResolveAlertAsync(open.Id, now.ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false))
                    {
                        await AuditAsync("alert.resolve", rule.Name, open.Id, now, cancellationToken).ConfigureAwait(false);
                        await NotifyAsync(open, rule, "resolved", cancellationToken).ConfigureAwait(false);
                        resolved++;
                    }
                }
            }
        }

        return (opened, resolved);
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

    private async Task NotifyAsync(OpsAlertRow alert, AlertRule rule, string transition, CancellationToken cancellationToken)
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
            ? $"[{rule.Severity}] {rule.Name}"
            : $"[已恢复] {rule.Name}";

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
                        rule = rule.Name,
                        severity = rule.Severity,
                        value = alert.Value,
                        openedAt = alert.OpenedAt,
                        resolvedAt = transition == "resolved" ? now : (long?)null,
                    },
                },
                OpsEventJson.Options);

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(new Uri(url), content, cancellationToken).ConfigureAwait(false);

            var sent = response.IsSuccessStatusCode;
            await store.RecordNotificationAttemptAsync(
                alert.Id,
                NotificationChannel,
                transition,
                sent,
                sent ? null : $"HTTP {(int)response.StatusCode}",
                now,
                cancellationToken).ConfigureAwait(false);

            if (!sent)
            {
                NotifyRejected(logger, rule.Name, (int)response.StatusCode);
            }
        }
        catch (HttpRequestException ex)
        {
            await store.RecordNotificationAttemptAsync(
                alert.Id,
                NotificationChannel,
                transition,
                sent: false,
                ex.Message,
                now,
                cancellationToken).ConfigureAwait(false);
            NotifyFailed(logger, rule.Name, ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await store.RecordNotificationAttemptAsync(
                alert.Id,
                NotificationChannel,
                transition,
                sent: false,
                "timed out",
                now,
                cancellationToken).ConfigureAwait(false);
            NotifyFailed(logger, rule.Name, "timed out");
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
}
