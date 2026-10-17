using System.Data.Common;

namespace RemoteDesktop.Server.Ops.Storage;

/// <summary>One management action in the two-person review queue, as the page reads it.</summary>
public sealed record OpsAdminActionRow(
    string Id,
    string Kind,
    string NodeId,
    string DeviceId,
    string? SessionId,
    string DetailJson,
    string State,
    string RequestedBy,
    long RequestedAt,
    string? ApprovedBy,
    long? ApprovedAt,
    long? ExecutedAt,
    string? Error);

/// <summary>The states an action walks through, from an operator's intent to an approved, executed command.</summary>
public static class ActionStates
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Executed = "executed";
    public const string Failed = "failed";
}

/// <summary>The kinds of action. <c>revoke_device</c> with the device online also drops its signaling connection.</summary>
public static class ActionKinds
{
    public const string DisconnectSession = "disconnect_session";
    public const string RevokeDevice = "revoke_device";
    public const string UnrevokeDevice = "unrevoke_device";
}

public sealed partial class OpsStore
{
    private const string ActionColumns =
        "id, kind, node_id, device_id, session_id, detail_json, state, requested_by, requested_at, approved_by, approved_at, executed_at, error";

    /// <summary>Inserts a pending action. The id is the caller's, minted once and carried into the node's command.</summary>
    public async Task CreateActionAsync(OpsAdminActionRow action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ops_admin_action (id, kind, node_id, device_id, session_id, detail_json, state,
                                          requested_by, requested_at)
            VALUES (@id, @kind, @nodeId, @deviceId, @sessionId, @detailJson, @state, @requestedBy, @requestedAt);
            """;
        Bind(
            command,
            ("@id", action.Id),
            ("@kind", action.Kind),
            ("@nodeId", action.NodeId),
            ("@deviceId", action.DeviceId),
            ("@sessionId", action.SessionId),
            ("@detailJson", action.DetailJson),
            ("@state", action.State),
            ("@requestedBy", action.RequestedBy),
            ("@requestedAt", action.RequestedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Actions, newest first. <paramref name="state"/> filters; omit for all.</summary>
    public async Task<IReadOnlyList<OpsAdminActionRow>> ListActionsAsync(string? state, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ActionColumns} FROM ops_admin_action
            WHERE (@state IS NULL OR state = @state)
            ORDER BY requested_at DESC
            LIMIT @limit;
            """;
        Bind(command, ("@state", string.IsNullOrEmpty(state) ? null : state), ("@limit", Math.Clamp(limit, 1, 200)));

        var actions = new List<OpsAdminActionRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            actions.Add(ReadAction(reader));
        }

        return actions;
    }

    /// <summary>One action by id, or <c>null</c>.</summary>
    public async Task<OpsAdminActionRow?> FindActionAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ActionColumns} FROM ops_admin_action WHERE id = @id;";
        Bind(command, ("@id", id));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadAction(reader) : null;
    }

    /// <summary>
    /// Approves a pending action, refusing when the approver is the one who asked. Returns the updated row, or
    /// <c>null</c> when the action is not pending or the approver is the requester — the caller turns both into a
    /// refusal, but with a different message.
    /// </summary>
    public async Task<OpsAdminActionRow?> ApproveActionAsync(string id, string actor, long at, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ops_admin_action
            SET state = 'approved', approved_by = @actor, approved_at = @at
            WHERE id = @id AND state = 'pending' AND requested_by <> @actor;
            """;
        Bind(command, ("@id", id), ("@actor", actor), ("@at", at));

        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            return null;
        }

        return await FindActionAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Marks an approved action as executed, or as failed with a reason.</summary>
    public async Task CompleteActionAsync(string id, string state, long at, string? error, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ops_admin_action SET state = @state, executed_at = @at, error = @error
            WHERE id = @id AND state = 'approved';
            """;
        Bind(command, ("@id", id), ("@state", state), ("@at", at), ("@error", error));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static OpsAdminActionRow ReadAction(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetInt64(10),
            reader.IsDBNull(11) ? null : reader.GetInt64(11),
            reader.IsDBNull(12) ? null : reader.GetString(12));
}
