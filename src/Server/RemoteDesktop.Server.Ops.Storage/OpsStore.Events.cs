using System.Data.Common;
using System.Globalization;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops.Storage;

/// <summary>One row of the session projection, as the page reads it.</summary>
public sealed record OpsSessionRow(
    string Id,
    string CorrelationId,
    uint? ControllerDeviceId,
    uint? HostDeviceId,
    string? Mode,
    string? Transport,
    string Source,
    string? RelayNodeId,
    long? StartedAt,
    long? EndedAt,
    string? EndReason,
    long? BytesIn,
    long? BytesOut,
    long UpdatedAt,
    double? RttMs,
    double? LossPct,
    string? Platform,
    string? Version,
    string? ClientSessionId);

/// <summary>One stored event, for the detail view's timeline.</summary>
public sealed record OpsEventRow(
    string EventId,
    string Kind,
    string Node,
    long OccurredAt,
    long ReceivedAt,
    string Payload);

/// <summary>
/// How the sessions of a window split by link and by platform. Only sessions a client reported on are counted: a row
/// the relay produced without a heartbeat says nothing about either, and counting it as "unknown" would make the
/// percentages mean less than they appear to.
/// </summary>
public sealed record SessionMix(
    IReadOnlyList<KeyValuePair<string, int>> Platforms,
    int Direct,
    int Relayed);

public sealed partial class OpsStore
{
    private const string SessionColumns =
        "id, correlation_id, controller_device_id, host_device_id, mode, transport, source, relay_node_id, "
        + "started_at, ended_at, end_reason, bytes_in, bytes_out, updated_at, rtt_ms, loss_pct, platform, version, client_session_id";

    /// <summary>
    /// Stores the events and folds them into their sessions, in one transaction. Returns how many events were new.
    /// </summary>
    /// <remarks>
    /// Idempotency is the unique index on <c>event_id</c>, not the caller's promise: a retried batch inserts nothing and
    /// changes no projection. The session is read once per correlation id per batch, so a batch holding a whole
    /// connection's life does not re-read it for every event.
    /// </remarks>
    public async Task<int> AppendEventsAsync(
        IReadOnlyList<OpsEventEnvelope> events,
        long receivedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return 0;
        }

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var inserted = 0;
        var sessions = new Dictionary<string, SessionState?>(StringComparer.Ordinal);

        foreach (var envelope in events)
        {
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO ops_event (id, event_id, node, kind, occurred_at, received_at, correlation_id, payload, schema_version)
                    VALUES (@id, @eventId, @node, @kind, @occurredAt, @receivedAt, @correlationId, @payload, @version)
                    ON CONFLICT (event_id) DO NOTHING;
                    """;
                Bind(
                    insert,
                    ("@id", envelope.EventId),
                    ("@eventId", envelope.EventId),
                    ("@node", envelope.Node),
                    ("@kind", envelope.Kind),
                    ("@occurredAt", envelope.OccurredAt.ToUnixTimeMilliseconds()),
                    ("@receivedAt", receivedAt),
                    ("@correlationId", envelope.CorrelationId),
                    ("@payload", envelope.Payload),
                    ("@version", envelope.V));

                if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                {
                    continue;
                }
            }

            inserted++;

            if (!sessions.TryGetValue(envelope.CorrelationId, out var session))
            {
                session = await ReadSessionAsync(connection, transaction, envelope.CorrelationId, cancellationToken).ConfigureAwait(false);
            }

            var projected = SessionProjection.Apply(session, envelope);
            sessions[envelope.CorrelationId] = projected;

            if (projected is not null && !ReferenceEquals(projected, session))
            {
                await UpsertSessionAsync(connection, transaction, projected, cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return inserted;
    }

    /// <summary>Recent sessions, newest first. <paramref name="before"/> pages by start time.</summary>
    public async Task<IReadOnlyList<OpsSessionRow>> ListSessionsAsync(
        int limit,
        long? before,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SessionColumns} FROM ops_session
            WHERE CAST(@before AS BIGINT) IS NULL OR COALESCE(started_at, updated_at) < @before
            ORDER BY COALESCE(started_at, updated_at) DESC
            LIMIT @limit;
            """;
        Bind(command, ("@before", before), ("@limit", Math.Clamp(limit, 1, 200)));

        var rows = new List<OpsSessionRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadSessionRow(reader));
        }

        return rows;
    }

    /// <summary>The session with this console id, or <c>null</c>.</summary>
    public async Task<OpsSessionRow?> FindSessionAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SessionColumns} FROM ops_session WHERE id = @id;";
        Bind(command, ("@id", id));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSessionRow(reader) : null;
    }

    /// <summary>How the sessions started since <paramref name="sinceUnix"/> split by link and platform.</summary>
    /// <remarks>
    /// Two grouped reads rather than one row per session: a year of sessions is not something a page should page
    /// through to draw four bars. Both only see rows a client reported on, because the relay knows neither.
    /// </remarks>
    public async Task<SessionMix> SummarizeSessionsAsync(long sinceUnix, CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);

        var platforms = new List<KeyValuePair<string, int>>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT platform, COUNT(*) FROM ops_session
                WHERE platform IS NOT NULL AND COALESCE(started_at, updated_at) >= @since
                GROUP BY platform ORDER BY COUNT(*) DESC;
                """;
            Bind(command, ("@since", sinceUnix));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                platforms.Add(new KeyValuePair<string, int>(reader.GetString(0), (int)reader.GetInt64(1)));
            }
        }

        var direct = 0;
        var relayed = 0;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT transport, COUNT(*) FROM ops_session
                WHERE transport IN ('direct', 'relay') AND COALESCE(started_at, updated_at) >= @since
                GROUP BY transport;
                """;
            Bind(command, ("@since", sinceUnix));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var count = (int)reader.GetInt64(1);
                if (string.Equals(reader.GetString(0), "direct", StringComparison.Ordinal))
                {
                    direct = count;
                }
                else
                {
                    relayed = count;
                }
            }
        }

        return new SessionMix(platforms, direct, relayed);
    }

    /// <summary>Mean round-trip time over the sessions of a window that reported one, or <c>null</c> when none did.</summary>
    public async Task<double?> AverageRttAsync(long sinceUnix, CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT AVG(rtt_ms) FROM ops_session
            WHERE rtt_ms IS NOT NULL AND COALESCE(started_at, updated_at) >= @since;
            """;
        Bind(command, ("@since", sinceUnix));

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    /// <summary>The events behind a session, oldest first.</summary>
    public async Task<IReadOnlyList<OpsEventRow>> ReadSessionEventsAsync(
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(correlationId);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, kind, node, occurred_at, received_at, payload
            FROM ops_event WHERE correlation_id = @correlationId
            ORDER BY occurred_at, received_at;
            """;
        Bind(command, ("@correlationId", correlationId));

        var rows = new List<OpsEventRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new OpsEventRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetString(5)));
        }

        return rows;
    }

    private static async Task<SessionState?> ReadSessionAsync(
        DbConnection connection,
        DbTransaction transaction,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {SessionColumns} FROM ops_session WHERE correlation_id = @correlationId;
            """;
        Bind(command, ("@correlationId", correlationId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var row = ReadSessionRow(reader);
        return new SessionState(
            row.Id,
            row.CorrelationId,
            row.ControllerDeviceId,
            row.HostDeviceId,
            row.Mode,
            row.Transport,
            row.Source,
            row.RelayNodeId,
            row.StartedAt,
            row.EndedAt,
            row.EndReason,
            row.BytesIn,
            row.BytesOut,
            row.UpdatedAt,
            row.RttMs,
            row.LossPct,
            row.Platform,
            row.Version,
            row.ClientSessionId);
    }

    private static async Task UpsertSessionAsync(
        DbConnection connection,
        DbTransaction transaction,
        SessionState session,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ops_session (id, correlation_id, controller_device_id, host_device_id, mode, transport, source,
                                     relay_node_id, started_at, ended_at, end_reason, bytes_in, bytes_out, rtt_ms, loss_pct,
                                     platform, version, client_session_id, updated_at)
            VALUES (@id, @correlationId, @controllerDeviceId, @hostDeviceId, @mode, @transport, @source,
                    @relayNodeId, @startedAt, @endedAt, @endReason, @bytesIn, @bytesOut, @rttMs, @lossPct,
                    @platform, @version, @clientSessionId, @updatedAt)
            ON CONFLICT (correlation_id) DO UPDATE SET
                controller_device_id = excluded.controller_device_id,
                host_device_id = excluded.host_device_id,
                mode = excluded.mode,
                transport = excluded.transport,
                source = excluded.source,
                relay_node_id = excluded.relay_node_id,
                started_at = excluded.started_at,
                ended_at = excluded.ended_at,
                end_reason = excluded.end_reason,
                bytes_in = excluded.bytes_in,
                bytes_out = excluded.bytes_out,
                rtt_ms = excluded.rtt_ms,
                loss_pct = excluded.loss_pct,
                platform = excluded.platform,
                version = excluded.version,
                client_session_id = excluded.client_session_id,
                updated_at = excluded.updated_at;
            """;
        Bind(
            command,
            ("@id", session.Id),
            ("@correlationId", session.CorrelationId),
            ("@controllerDeviceId", session.ControllerDeviceId is { } controller ? (long)controller : null),
            ("@hostDeviceId", session.HostDeviceId is { } host ? (long)host : null),
            ("@mode", session.Mode),
            ("@transport", session.Transport),
            ("@source", session.Source),
            ("@relayNodeId", session.RelayNodeId),
            ("@startedAt", session.StartedAt),
            ("@endedAt", session.EndedAt),
            ("@endReason", session.EndReason),
            ("@bytesIn", session.BytesIn),
            ("@bytesOut", session.BytesOut),
            ("@rttMs", session.RttMs),
            ("@lossPct", session.LossPct),
            ("@platform", session.Platform),
            ("@version", session.Version),
            ("@clientSessionId", session.ClientSessionId),
            ("@updatedAt", session.UpdatedAt));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static OpsSessionRow ReadSessionRow(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            ReadDeviceId(reader, 2),
            ReadDeviceId(reader, 3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetInt64(11),
            reader.IsDBNull(12) ? null : reader.GetInt64(12),
            reader.GetInt64(13),
            reader.IsDBNull(14) ? null : reader.GetDouble(14),
            reader.IsDBNull(15) ? null : reader.GetDouble(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18));

    private static uint? ReadDeviceId(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : (uint)reader.GetInt64(ordinal);
}
