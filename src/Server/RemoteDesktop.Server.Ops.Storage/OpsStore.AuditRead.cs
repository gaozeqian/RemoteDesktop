using System.Data.Common;
using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.Storage;

public sealed partial class OpsStore
{
    private const string AuditColumns =
        "id, ts, actor, action, target, detail_json, ip, result, prev_hash, hash";

    /// <summary>Audit records, newest first, paged by timestamp.</summary>
    public async Task<IReadOnlyList<AuditRecord>> ListAuditAsync(
        int limit,
        long? before,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {AuditColumns} FROM ops_audit_log
            WHERE CAST(@before AS BIGINT) IS NULL OR ts < @before
            ORDER BY ts DESC, id DESC
            LIMIT @limit;
            """;
        Bind(command, ("@before", before), ("@limit", Math.Clamp(limit, 1, 500)));

        return await ReadAuditAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The whole chain, oldest first, for verification.
    /// </summary>
    /// <remarks>
    /// Deliberately unpaged: a link can only be checked against the record before it, so verifying a page would mean
    /// calling the oldest row on that page a liar. The log is small in this version — one row per sign-in and per
    /// administrative action — and when it is not, the fix is an archive with its own chain anchor, not a limit here.
    /// </remarks>
    public async Task<IReadOnlyList<AuditRecord>> ReadAuditChainAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {AuditColumns} FROM ops_audit_log ORDER BY ts ASC, id ASC;";

        return await ReadAuditAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The kind the catalog recorded for a series, or <c>null</c> when it has never been seen.</summary>
    public async Task<string?> ReadMetricKindAsync(string metric, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(metric);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind FROM ops_metric_catalog WHERE metric = @metric;";
        Bind(command, ("@metric", metric));

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static async Task<IReadOnlyList<AuditRecord>> ReadAuditAsync(DbCommand command, CancellationToken cancellationToken)
    {
        var records = new List<AuditRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new AuditRecord(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9)));
        }

        return records;
    }
}
