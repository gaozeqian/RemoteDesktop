using System.Data.Common;
using System.Globalization;
using System.Text;
using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.Storage;

/// <summary>One stored reading. <see cref="Metric"/> is the series key: name plus canonical labels.</summary>
public readonly record struct MetricPoint(string NodeId, string Metric, long Timestamp, double Value);

public sealed partial class OpsStore
{
    private const int SampleBatchSize = 200;
    private const int CatalogBatchSize = 200;

    /// <summary>
    /// Records or corrects a metric's type. The catalog is what stops a counter from being averaged: the type travels
    /// with the series, so a query never has to guess from the name.
    /// </summary>
    public async Task UpsertMetricCatalogAsync(
        IReadOnlyCollection<KeyValuePair<string, string>> metrics,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        if (metrics.Count == 0)
        {
            return;
        }

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var chunk in metrics.Chunk(CatalogBatchSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = UpsertCatalogSql(chunk.Length);
            BindCatalog(command, chunk);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes one scrape. Duplicate readings for the same node, series and timestamp replace the earlier one, so a
    /// retried scrape cannot double-count.
    /// </summary>
    public async Task WriteSamplesAsync(
        string nodeId,
        long timestamp,
        IReadOnlyList<PrometheusSample> samples,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return;
        }

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var chunk in samples.Chunk(SampleBatchSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = UpsertSamplesSql(chunk.Length);

            for (var index = 0; index < chunk.Length; index++)
            {
                Bind(
                    command,
                    ($"@n{index}", nodeId),
                    ($"@m{index}", chunk[index].Key),
                    ($"@t{index}", timestamp),
                    ($"@v{index}", chunk[index].Value));
            }

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The newest reading per node and series at or after <paramref name="sinceUnixMilliseconds"/>.
    /// </summary>
    /// <remarks>
    /// A window function rather than a correlated subquery per row: both providers support it, and the console asks this
    /// on every overview request. Readings older than the cutoff are treated as absent rather than stale-but-valid —
    /// showing yesterday's online count as today's is the failure this cutoff exists to prevent.
    /// </remarks>
    public async Task<IReadOnlyList<MetricPoint>> ReadLatestAsync(
        IReadOnlyCollection<string> metrics,
        long sinceUnixMilliseconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        if (metrics.Count == 0)
        {
            return [];
        }

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT node_id, metric, ts, value FROM (
                SELECT node_id, metric, ts, value,
                       ROW_NUMBER() OVER (PARTITION BY node_id, metric ORDER BY ts DESC) AS rn
                FROM ops_metric_sample
                WHERE metric IN ({Placeholders(metrics.Count, "m")}) AND ts >= @since
            ) AS newest
            WHERE rn = 1;
            """;
        BindMetrics(command, metrics);
        Bind(command, ("@since", sinceUnixMilliseconds));

        return await ReadPointsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every reading of <paramref name="metrics"/> in the window, ordered by series and then time.</summary>
    public async Task<IReadOnlyList<MetricPoint>> ReadSeriesAsync(
        IReadOnlyCollection<string> metrics,
        long fromUnixMilliseconds,
        long toUnixMilliseconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        if (metrics.Count == 0)
        {
            return [];
        }

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT node_id, metric, ts, value FROM ops_metric_sample
            WHERE metric IN ({Placeholders(metrics.Count, "m")}) AND ts >= @from AND ts <= @to
            ORDER BY metric, node_id, ts;
            """;
        BindMetrics(command, metrics);
        Bind(command, ("@from", fromUnixMilliseconds), ("@to", toUnixMilliseconds));

        return await ReadPointsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes readings older than <paramref name="beforeUnixMilliseconds"/> in bounded chunks.</summary>
    public async Task<int> DeleteSamplesBeforeAsync(
        long beforeUnixMilliseconds,
        int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // A single DELETE over a week of readings locks the database for minutes. The limit keeps each statement short
        // enough that a page load in between still gets through; the caller loops until it returns fewer than the batch.
        command.CommandText = """
            DELETE FROM ops_metric_sample
            WHERE (node_id, metric, ts) IN (
                SELECT node_id, metric, ts FROM ops_metric_sample WHERE ts < @before LIMIT @limit
            );
            """;
        Bind(command, ("@before", beforeUnixMilliseconds), ("@limit", Math.Clamp(limit, 1, 20_000)));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<MetricPoint>> ReadPointsAsync(DbCommand command, CancellationToken cancellationToken)
    {
        var points = new List<MetricPoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            points.Add(new MetricPoint(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetDouble(3)));
        }

        return points;
    }

    private static string Placeholders(int count, string prefix)
    {
        var builder = new StringBuilder(count * 5);
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append('@').Append(prefix).Append(index);
        }

        return builder.ToString();
    }

    private static void BindMetrics(DbCommand command, IReadOnlyCollection<string> metrics)
    {
        var index = 0;
        foreach (var metric in metrics)
        {
            Bind(command, ($"@m{index}", metric));
            index++;
        }
    }

    private static string UpsertCatalogSql(int rows) =>
        $"INSERT INTO ops_metric_catalog (metric, kind, unit) VALUES {ValueRows(rows, "(@m{i}, @k{i}, NULL)")} "
        + "ON CONFLICT (metric) DO UPDATE SET kind = excluded.kind;";

    private static void BindCatalog(DbCommand command, KeyValuePair<string, string>[] chunk)
    {
        for (var index = 0; index < chunk.Length; index++)
        {
            Bind(command, ($"@m{index}", chunk[index].Key), ($"@k{index}", chunk[index].Value));
        }
    }

    private static string UpsertSamplesSql(int rows) =>
        $"INSERT INTO ops_metric_sample (node_id, metric, ts, value) VALUES {ValueRows(rows, "(@n{i}, @m{i}, @t{i}, @v{i})")} "
        + "ON CONFLICT (node_id, metric, ts) DO UPDATE SET value = excluded.value;";

    private static string ValueRows(int rows, string pattern)
    {
        var builder = new StringBuilder(rows * pattern.Length);
        for (var index = 0; index < rows; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(pattern.Replace("{i}", index.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        }

        return builder.ToString();
    }
}
