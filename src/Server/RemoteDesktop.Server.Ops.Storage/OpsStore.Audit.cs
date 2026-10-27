using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
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
    /// The previous hash is read and the new row written inside one transaction. The in-process semaphore only makes
    /// the common single-console case cheap; the transaction is what makes the pair safe when another process appends
    /// too — the setup CLI creating an administrator while the service is running is exactly that case. SQLite gets a
    /// write lock up front with <c>BEGIN IMMEDIATE</c>; PostgreSQL takes an advisory transaction lock.
    /// </remarks>
    public async Task<string> AppendAuditAsync(OpsAuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await _auditGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await BeginAuditTransactionAsync(connection, cancellationToken).ConfigureAwait(false);

            var (previous, previousTimestamp) = await ReadLatestAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            // Verification walks the chain in (ts, id) order, so the new row has to be last in that order too. Callers
            // often write several records with one captured "now" (the alert evaluator does), and ids are random, so
            // two rows with the same millisecond could sort either way: the second row could point at a row that
            // verification visits after it, and an untouched log would be reported as tampered. Keeping the stored
            // timestamp strictly increasing makes the append order and the verification order the same thing.
            var timestamp = Math.Max(entry.Timestamp, previousTimestamp + 1);
            var hash = AuditChain.Compute(
                previous,
                AuditChain.Fields(
                    timestamp,
                    entry.Actor,
                    entry.Action,
                    entry.Target,
                    entry.DetailJson,
                    entry.Ip,
                    entry.Result));

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ops_audit_log (id, ts, actor, action, target, detail_json, ip, result, prev_hash, hash)
                VALUES (@id, @ts, @actor, @action, @target, @detail, @ip, @result, @prevHash, @hash);
                """;
            Bind(
                command,
                ("@id", entry.Id),
                ("@ts", timestamp),
                ("@actor", entry.Actor),
                ("@action", entry.Action),
                ("@target", entry.Target),
                ("@detail", entry.DetailJson),
                ("@ip", entry.Ip),
                ("@result", entry.Result),
                ("@prevHash", previous),
                ("@hash", hash));

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return hash;
        }
        finally
        {
            _auditGate.Release();
        }
    }

    /// <summary>
    /// Starts the transaction that holds the chain's read-and-append pair, taking the database's write lock first.
    /// </summary>
    /// <remarks>
    /// SQLite's ordinary (deferred) transaction reads before it writes, so two processes can read the same previous
    /// hash; <c>BEGIN IMMEDIATE</c> takes the write lock at once and the second process waits. PostgreSQL has no
    /// database-wide write lock, so an advisory transaction lock gives the same serialisation.
    /// </remarks>
    private async Task<DbTransaction> BeginAuditTransactionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (string.Equals(_provider, OpsDatabaseSettings.Sqlite, StringComparison.Ordinal))
        {
            return ((SqliteConnection)connection).BeginTransaction(deferred: false);
        }

        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var advisory = connection.CreateCommand();
        advisory.Transaction = transaction;
        advisory.CommandText = "SELECT pg_advisory_xact_lock(hashtext('remotedesktop.ops.audit'));";
        await advisory.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return transaction;
    }

    /// <summary>
    /// The newest hash and timestamp, or <see cref="AuditChain.Genesis"/> and <c>long.MinValue</c> when the log is
    /// empty.
    /// </summary>
    private static async Task<(string Hash, long Timestamp)> ReadLatestAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT hash, ts FROM ops_audit_log ORDER BY ts DESC, id DESC LIMIT 1;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (AuditChain.Genesis, long.MinValue);
        }

        return (reader.GetString(0), reader.GetInt64(1));
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
