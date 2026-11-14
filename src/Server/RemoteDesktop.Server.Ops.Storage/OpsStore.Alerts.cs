using System.Data.Common;
using System.Globalization;
using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.Storage;

/// <summary>One alert as the page reads it.</summary>
public sealed record OpsAlertRow(
    string Id,
    string RuleId,
    string Severity,
    string DedupeKey,
    string State,
    double? Value,
    string LabelsJson,
    long OpenedAt,
    long? AckedAt,
    string? AckedBy,
    long? ResolvedAt);

/// <summary>One notification that still needs a successful delivery, with the alert it describes.</summary>
public sealed record OpsPendingNotification(
    string AlertId,
    string RuleId,
    string RuleName,
    string Severity,
    string DedupeKey,
    string State,
    double? Value,
    long OpenedAt,
    long? ResolvedAt,
    string Transition,
    int Attempts,
    long? LastAttemptAt);

public sealed partial class OpsStore
{
    private const string AlertColumns =
        "id, rule_id, severity, dedupe_key, state, value, labels_json, opened_at, acked_at, acked_by, resolved_at";

    private const string RuleColumns =
        "id, name, kind, metric, numerator, denominator, aggregation, comparator, threshold, window_seconds, for_seconds, severity, enabled";

    /// <summary>Every rule, by name. Disabled ones included: an operator needs to see what is switched off.</summary>
    public async Task<IReadOnlyList<AlertRule>> ListAlertRulesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {RuleColumns} FROM ops_alert_rule ORDER BY name;";

        var rules = new List<AlertRule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rules.Add(ReadRule(reader));
        }

        return rules;
    }

    /// <summary>Inserts or replaces a rule by id.</summary>
    public async Task UpsertAlertRuleAsync(AlertRule rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ops_alert_rule (id, name, kind, metric, numerator, denominator, aggregation, comparator,
                                        threshold, window_seconds, for_seconds, severity, enabled)
            VALUES (@id, @name, @kind, @metric, @numerator, @denominator, @aggregation, @comparator,
                    @threshold, @windowSeconds, @forSeconds, @severity, @enabled)
            ON CONFLICT (id) DO UPDATE SET
                name = excluded.name, kind = excluded.kind, metric = excluded.metric,
                numerator = excluded.numerator, denominator = excluded.denominator,
                aggregation = excluded.aggregation, comparator = excluded.comparator,
                threshold = excluded.threshold, window_seconds = excluded.window_seconds,
                for_seconds = excluded.for_seconds, severity = excluded.severity, enabled = excluded.enabled;
            """;
        Bind(
            command,
            ("@id", rule.Id),
            ("@name", rule.Name),
            ("@kind", rule.Kind),
            ("@metric", rule.Metric),
            ("@numerator", rule.Numerator),
            ("@denominator", rule.Denominator),
            ("@aggregation", rule.Aggregation),
            ("@comparator", rule.Comparator),
            ("@threshold", rule.Threshold),
            ("@windowSeconds", rule.WindowSeconds),
            ("@forSeconds", rule.ForSeconds),
            ("@severity", rule.Severity),
            ("@enabled", rule.Enabled ? 1 : 0));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes a rule. Returns false when the id is unknown.</summary>
    public async Task<bool> DeleteAlertRuleAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ops_alert_rule WHERE id = @id;";
        Bind(command, ("@id", id));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>Number of rules, for the empty state.</summary>
    public async Task<long> CountAlertRulesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ops_alert_rule;";
        var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(count, CultureInfo.InvariantCulture);
    }

    /// <summary>Alerts, newest first; <paramref name="state"/> filters to <c>open</c> or <c>resolved</c>.</summary>
    public async Task<IReadOnlyList<OpsAlertRow>> ListAlertsAsync(
        string? state,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {AlertColumns} FROM ops_alert
            WHERE CAST(@state AS TEXT) IS NULL OR state = @state
            ORDER BY opened_at DESC
            LIMIT @limit;
            """;
        Bind(command, ("@state", string.IsNullOrEmpty(state) ? null : state), ("@limit", Math.Clamp(limit, 1, 200)));

        var rows = new List<OpsAlertRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadAlert(reader));
        }

        return rows;
    }

    /// <summary>
    /// Every alert still open, across all rules. The evaluator reads this to find alerts whose signal has disappeared —
    /// a deleted node, a disabled rule, or a rule that was removed — because those can never be resolved by comparing
    /// a current reading with a threshold.
    /// </summary>
    public async Task<IReadOnlyList<OpsAlertRow>> ListOpenAlertsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {AlertColumns} FROM ops_alert WHERE state = 'open' ORDER BY opened_at;";

        var rows = new List<OpsAlertRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadAlert(reader));
        }

        return rows;
    }

    /// <summary>The open alert for <paramref name="dedupeKey"/>, if one exists.</summary>
    public async Task<OpsAlertRow?> FindOpenAlertAsync(string dedupeKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(dedupeKey);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {AlertColumns} FROM ops_alert WHERE dedupe_key = @key AND state = 'open';";
        Bind(command, ("@key", dedupeKey));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadAlert(reader) : null;
    }

    /// <summary>Records a new open alert.</summary>
    public async Task OpenAlertAsync(OpsAlertRow alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ops_alert (id, rule_id, severity, dedupe_key, state, value, labels_json,
                                   opened_at, acked_at, acked_by, resolved_at)
            VALUES (@id, @ruleId, @severity, @dedupeKey, 'open', @value, @labels, @openedAt, NULL, NULL, NULL);
            """;
        Bind(
            command,
            ("@id", alert.Id),
            ("@ruleId", alert.RuleId),
            ("@severity", alert.Severity),
            ("@dedupeKey", alert.DedupeKey),
            ("@value", alert.Value),
            ("@labels", alert.LabelsJson),
            ("@openedAt", alert.OpenedAt));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes an alert. Returns false when it was already closed or unknown.</summary>
    public async Task<bool> ResolveAlertAsync(string id, long at, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ops_alert SET state = 'resolved', resolved_at = @at WHERE id = @id AND state = 'open';";
        Bind(command, ("@id", id), ("@at", at));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>Marks an alert as acknowledged. Returns false when it is unknown or already resolved.</summary>
    public async Task<bool> AckAlertAsync(string id, string by, long at, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ops_alert SET acked_at = @at, acked_by = @by
            WHERE id = @id AND state = 'open' AND acked_at IS NULL;
            """;
        Bind(command, ("@id", id), ("@at", at), ("@by", by));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>How many alerts opened since <paramref name="sinceUnixMilliseconds"/>; the overview's "today".</summary>
    public async Task<long> CountAlertsSinceAsync(long sinceUnixMilliseconds, CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ops_alert WHERE opened_at >= @since;";
        Bind(command, ("@since", sinceUnixMilliseconds));

        var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(count, CultureInfo.InvariantCulture);
    }

    /// <summary>Whether a notification for this alert and transition was already delivered.</summary>
    public async Task<bool> HasNotificationAttemptAsync(
        string alertId,
        string channelId,
        string transition,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sent_at FROM ops_notification_attempt
            WHERE alert_id = @alertId AND channel_id = @channelId AND transition = @transition;
            """;
        Bind(command, ("@alertId", alertId), ("@channelId", channelId), ("@transition", transition));

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is not (null or DBNull);
    }

    /// <summary>Records a delivery attempt: success stamps <c>sent_at</c>, failure keeps the count and the reason.</summary>
    public async Task RecordNotificationAttemptAsync(
        string alertId,
        string channelId,
        string transition,
        bool sent,
        string? error,
        long at,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ops_notification_attempt (alert_id, channel_id, transition, attempts, last_error, sent_at, last_attempt_at)
            VALUES (@alertId, @channelId, @transition, 1, @error, @sentAt, @lastAttemptAt)
            ON CONFLICT (alert_id, channel_id, transition) DO UPDATE SET
                attempts = ops_notification_attempt.attempts + 1,
                last_error = excluded.last_error,
                sent_at = COALESCE(excluded.sent_at, ops_notification_attempt.sent_at),
                last_attempt_at = excluded.last_attempt_at;
            """;
        Bind(
            command,
            ("@alertId", alertId),
            ("@channelId", channelId),
            ("@transition", transition),
            ("@error", error),
            ("@sentAt", sent ? at : null),
            ("@lastAttemptAt", at));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Notifications that have not been delivered and whose alert is still in the state that transition describes.
    /// A failed "opened" is not retried after the alert has resolved: the recovery message is the useful one then.
    /// </summary>
    public async Task<IReadOnlyList<OpsPendingNotification>> ListPendingNotificationsAsync(
        string channelId,
        int maxAttempts,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id, a.rule_id, COALESCE(r.name, a.dedupe_key), a.severity, a.dedupe_key, a.state,
                   a.value, a.opened_at, a.resolved_at, n.transition, n.attempts, n.last_attempt_at
            FROM ops_notification_attempt n
            JOIN ops_alert a ON a.id = n.alert_id
            LEFT JOIN ops_alert_rule r ON r.id = a.rule_id
            WHERE n.channel_id = @channel
              AND n.sent_at IS NULL
              AND n.attempts < @maxAttempts
              AND ((a.state = 'open' AND n.transition = 'opened')
                   OR (a.state = 'resolved' AND n.transition = 'resolved'))
            ORDER BY COALESCE(n.last_attempt_at, 0)
            LIMIT @limit;
            """;
        Bind(
            command,
            ("@channel", channelId),
            ("@maxAttempts", Math.Max(1, maxAttempts)),
            ("@limit", Math.Clamp(limit, 1, 1000)));

        var rows = new List<OpsPendingNotification>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new OpsPendingNotification(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetInt64(8),
                reader.GetString(9),
                reader.GetInt32(10),
                reader.IsDBNull(11) ? null : reader.GetInt64(11)));
        }

        return rows;
    }

    /// <summary>
    /// Deletes resolved alerts older than the cutoff, with their notification attempts, in bounded batches.
    /// Open alerts are never deleted, however old: an unresolved alert is still a statement about the present.
    /// </summary>
    public async Task<int> DeleteResolvedAlertsBeforeAsync(
        long beforeUnixMilliseconds,
        int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var batch = Math.Clamp(limit, 1, 20_000);

        await using (var attempts = connection.CreateCommand())
        {
            attempts.Transaction = transaction;
            attempts.CommandText = """
                DELETE FROM ops_notification_attempt WHERE alert_id IN (
                    SELECT id FROM ops_alert
                    WHERE state = 'resolved'
                      AND (resolved_at < @before OR (resolved_at IS NULL AND opened_at < @before))
                    ORDER BY id
                    LIMIT @limit
                );
                """;
            Bind(attempts, ("@before", beforeUnixMilliseconds), ("@limit", batch));
            await attempts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int deleted;
        await using (var alerts = connection.CreateCommand())
        {
            alerts.Transaction = transaction;
            alerts.CommandText = """
                DELETE FROM ops_alert WHERE id IN (
                    SELECT id FROM ops_alert
                    WHERE state = 'resolved'
                      AND (resolved_at < @before OR (resolved_at IS NULL AND opened_at < @before))
                    ORDER BY id
                    LIMIT @limit
                );
                """;
            Bind(alerts, ("@before", beforeUnixMilliseconds), ("@limit", batch));
            deleted = await alerts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    private static AlertRule ReadRule(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetDouble(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetString(11),
            reader.GetInt64(12) != 0);

    private static OpsAlertRow ReadAlert(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetDouble(5),
            reader.GetString(6),
            reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetInt64(10));
}
