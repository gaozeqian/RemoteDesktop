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
            INSERT INTO ops_notification_attempt (alert_id, channel_id, transition, attempts, last_error, sent_at)
            VALUES (@alertId, @channelId, @transition, 1, @error, @sentAt)
            ON CONFLICT (alert_id, channel_id, transition) DO UPDATE SET
                attempts = ops_notification_attempt.attempts + 1,
                last_error = excluded.last_error,
                sent_at = COALESCE(excluded.sent_at, ops_notification_attempt.sent_at);
            """;
        Bind(
            command,
            ("@alertId", alertId),
            ("@channelId", channelId),
            ("@transition", transition),
            ("@error", error),
            ("@sentAt", sent ? at : null));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
