using System.Data.Common;
using System.Globalization;
using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.Storage;

/// <summary>One audit record, already canonicalised by the caller. <see cref="Timestamp"/> is Unix milliseconds.</summary>
public sealed record OpsAuditEntry(
    string Id,
    long Timestamp,
    string Actor,
    string Action,
    string Target,
    string DetailJson,
    string Ip,
    string Result);

public sealed partial class OpsStore
{
    /// <summary>
    /// Appends one record and returns its hash, linking it to the previous one.
    /// </summary>
    /// <remarks>
    /// The previous hash is read and the new row written while holding an in-process semaphore. That is sound only
    /// because the console is a single instance (the plan's §3.3); a second writer would need the database to
    /// serialise the pair instead, which SQLite does with a write transaction and PostgreSQL with an advisory lock.
    /// When multi-instance arrives, this method changes — the table does not.
    /// </remarks>
    public async Task<string> AppendAuditAsync(OpsAuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await _auditGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);

            var previous = await ReadLatestHashAsync(connection, cancellationToken).ConfigureAwait(false);
            var hash = AuditChain.Compute(
                previous,
                AuditChain.Fields(
                    entry.Timestamp,
                    entry.Actor,
                    entry.Action,
                    entry.Target,
                    entry.DetailJson,
                    entry.Ip,
                    entry.Result));

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ops_audit_log (id, ts, actor, action, target, detail_json, ip, result, prev_hash, hash)
                VALUES (@id, @ts, @actor, @action, @target, @detail, @ip, @result, @prevHash, @hash);
                """;
            Bind(
                command,
                ("@id", entry.Id),
                ("@ts", entry.Timestamp),
                ("@actor", entry.Actor),
                ("@action", entry.Action),
                ("@target", entry.Target),
                ("@detail", entry.DetailJson),
                ("@ip", entry.Ip),
                ("@result", entry.Result),
                ("@prevHash", previous),
                ("@hash", hash));

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return hash;
        }
        finally
        {
            _auditGate.Release();
        }
    }

    /// <summary>The newest hash, or <see cref="AuditChain.Genesis"/> when the log is empty.</summary>
    private static async Task<string> ReadLatestHashAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT hash FROM ops_audit_log ORDER BY ts DESC, id DESC LIMIT 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as string ?? AuditChain.Genesis;
    }

    /// <summary>Number of audit records, for the shell's health line and for tests.</summary>
    public async Task<long> CountAuditEntriesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ops_audit_log;";
        var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(count, CultureInfo.InvariantCulture);
    }
}
