using System.Data.Common;
using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.Storage;

public sealed partial class OpsStore
{
    public async Task<IReadOnlyList<NotificationChannel>> ListNotificationChannelsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, kind, target, secret, enabled
            FROM ops_notification_channel
            ORDER BY id ASC;
            """;

        var list = new List<NotificationChannel>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new NotificationChannel(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4) != 0));
        }

        return list;
    }

    public async Task<NotificationChannel?> GetNotificationChannelAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, kind, target, secret, enabled
            FROM ops_notification_channel
            WHERE id = @id;
            """;
        Bind(command, ("@id", id));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new NotificationChannel(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4) != 0);
        }

        return null;
    }

    public async Task UpsertNotificationChannelAsync(NotificationChannel channel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentException.ThrowIfNullOrEmpty(channel.Id);
        ArgumentException.ThrowIfNullOrEmpty(channel.Kind);
        ArgumentException.ThrowIfNullOrEmpty(channel.Target);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ops_notification_channel (id, kind, target, secret, enabled)
            VALUES (@id, @kind, @target, @secret, @enabled)
            ON CONFLICT (id) DO UPDATE SET
                kind = excluded.kind,
                target = excluded.target,
                secret = excluded.secret,
                enabled = excluded.enabled;
            """;
        Bind(
            command,
            ("@id", channel.Id),
            ("@kind", channel.Kind),
            ("@target", channel.Target),
            ("@secret", channel.Secret),
            ("@enabled", channel.Enabled ? 1 : 0));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteNotificationChannelAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ops_notification_channel WHERE id = @id;";
        Bind(command, ("@id", id));

        var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rows > 0;
    }
}
