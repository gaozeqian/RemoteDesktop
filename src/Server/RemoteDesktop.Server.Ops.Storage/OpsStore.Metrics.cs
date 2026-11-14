using System.Data.Common;
using System.Globalization;
using System.Text;
using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.Storage;

/// <summary>One stored reading. <see cref="Metric"/> is the series key: name plus canonical labels.</summary>
public readonly record struct MetricPoint(string NodeId, string Metric, long Timestamp, double Value);

/// <summary>A series name and the Prometheus kind its first scrape recorded.</summary>
public readonly record struct MetricCatalogRow(string Metric, string Kind);

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

    /// <summary>Every known series and its kind, for a picker that names what this deployment actually scrapes.</summary>
    public async Task<IReadOnlyList<MetricCatalogRow>> ListMetricCatalogAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT metric, kind FROM ops_metric_catalog ORDER BY metric;";

        var rows = new List<MetricCatalogRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new MetricCatalogRow(reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    /// <summary>The newest reading for every series a single node has reported. No freshness cutoff: the page shows
    /// each sample's own time, so it never presents a stale value as current.</summary>
    public async Task<IReadOnlyList<MetricPoint>> ReadLatestForNodeAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT node_id, metric, ts, value FROM (
                SELECT node_id, metric, ts, value,
                       ROW_NUMBER() OVER (PARTITION BY metric ORDER BY ts DESC) AS rn
                FROM ops_metric_sample
                WHERE node_id = @nodeId
            ) AS newest
            WHERE rn = 1
            ORDER BY metric;
            """;
        Bind(command, ("@nodeId", nodeId));

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

    /// <summary>Deletes downsampled metric buckets whose whole bucket is older than the retention cutoff.</summary>
    public async Task<int> DeleteRollupsBeforeAsync(
        long beforeUnixMilliseconds,
        int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // A bucket is comparable to a timestamp only after multiplying by the step, and the step differs per row. The
        // join turns that expression into an equality on step_seconds plus a range on bucket — the shape of
        // ix_ops_metric_rollup_step_bucket — so a batch no longer scans the whole rollup table.
        command.CommandText = """
            DELETE FROM ops_metric_rollup
            WHERE (node_id, metric, step_seconds, bucket) IN (
                SELECT r.node_id, r.metric, r.step_seconds, r.bucket
                FROM (SELECT DISTINCT step_seconds FROM ops_metric_rollup) AS s
                JOIN ops_metric_rollup AS r
                  ON r.step_seconds = s.step_seconds
                 AND r.bucket < (@before / (s.step_seconds * 1000))
                LIMIT @limit
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

    /// <summary>
    /// Aggregates every raw sample up to <paramref name="beforeTimestamp"/> into <paramref name="stepSeconds"/>
    /// buckets in <c>ops_metric_rollup</c>. This is the full-range form used by tests and a deliberate backfill.
    /// </summary>
    /// <returns>Number of rollup buckets upserted.</returns>
    public Task<int> RollupSamplesAsync(
        long beforeTimestamp,
        int stepSeconds,
        CancellationToken cancellationToken = default) =>
        RollupCoreAsync(beforeTimestamp, stepSeconds, afterTimestamp: 0, cancellationToken);

    /// <summary>
    /// The scheduled pass: continues from the newest bucket already written, re-rolling that bucket so samples that
    /// arrived after the previous pass are folded in. Without a lower bound every pass re-aggregated and rewrote the
    /// whole retention window (30 days by default), growing with history rather than with new samples. A database
    /// that has never been rolled up gets one day, not the whole history: the old samples expire on their own, and
    /// the alternative is a first pass that holds the SQLite writer for minutes.
    /// </summary>
    public async Task<int> RollupSamplesIncrementallyAsync(
        long beforeTimestamp,
        int stepSeconds,
        CancellationToken cancellationToken = default)
    {
        if (stepSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stepSeconds), "stepSeconds must be greater than zero.");
        }

        var stepMillis = (long)stepSeconds * 1000;
        long afterTimestamp;
        await using (var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false))
        {
            var latestBucket = await ReadLatestRollupBucketAsync(connection, stepSeconds, cancellationToken).ConfigureAwait(false);
            afterTimestamp = latestBucket is { } bucket
                ? Math.Max(0, (bucket - 1) * stepMillis)
                : Math.Max(0, beforeTimestamp - (long)TimeSpan.FromHours(24).TotalMilliseconds);
        }

        return await RollupCoreAsync(beforeTimestamp, stepSeconds, afterTimestamp, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> RollupCoreAsync(
        long beforeTimestamp,
        int stepSeconds,
        long afterTimestamp,
        CancellationToken cancellationToken)
    {
        if (stepSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stepSeconds), "stepSeconds must be greater than zero.");
        }

        var stepMillis = (long)stepSeconds * 1000;
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);

        // A timestamp is unique within one node/metric series, so the ordered subqueries give the actual first and
        // last values rather than guessing from min/max/average. Keep this provider-neutral: SQLite and PostgreSQL
        // both support CTEs, correlated subqueries and LIMIT.
        await using var selectCommand = connection.CreateCommand();
        selectCommand.CommandText = """
            WITH bucketed AS (
                SELECT node_id, metric, ts, value, (ts / @stepMillis) AS bucket
                FROM ops_metric_sample
                WHERE ts >= @afterTs AND ts <= @beforeTs
            ), aggregates AS (
                SELECT node_id, metric, bucket,
                       MIN(value) AS min_val, MAX(value) AS max_val, AVG(value) AS avg_val, COUNT(*) AS cnt
                FROM bucketed
                GROUP BY node_id, metric, bucket
            )
            SELECT aggregates.node_id, aggregates.metric, aggregates.bucket,
                   (SELECT value FROM bucketed first_sample
                    WHERE first_sample.node_id = aggregates.node_id
                      AND first_sample.metric = aggregates.metric
                      AND first_sample.bucket = aggregates.bucket
                    ORDER BY first_sample.ts ASC LIMIT 1) AS first_val,
                   (SELECT value FROM bucketed last_sample
                    WHERE last_sample.node_id = aggregates.node_id
                      AND last_sample.metric = aggregates.metric
                      AND last_sample.bucket = aggregates.bucket
                    ORDER BY last_sample.ts DESC LIMIT 1) AS last_val,
                   aggregates.min_val, aggregates.max_val, aggregates.avg_val, aggregates.cnt
            FROM aggregates;
            """;
        Bind(
            selectCommand,
            ("@stepMillis", stepMillis),
            ("@afterTs", afterTimestamp),
            ("@beforeTs", beforeTimestamp));

        var rows = new List<(string NodeId, string Metric, long Bucket, double FirstVal, double LastVal, double MinVal, double MaxVal, double AvgVal, int Count)>();
        await using (var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetDouble(3),
                    reader.GetDouble(4),
                    reader.GetDouble(5),
                    reader.GetDouble(6),
                    reader.GetDouble(7),
                    reader.GetInt32(8)));
            }
        }

        if (rows.Count == 0)
        {
            return 0;
        }

        // A pass re-reads the newest bucket (to absorb a late sample) and the one before it. Most of the time
        // nothing in them changed, and writing identical rows every ten minutes is exactly the unbounded rewrite the
        // incremental pass exists to avoid. The predicate leaves an unchanged row untouched, so the return value is
        // the number of rows that actually changed: zero on a quiet pass, one when a sample landed in the current
        // bucket. Both providers report the affected-row count from this form.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var written = 0;
        foreach (var row in rows)
        {
            await using var upsertCommand = connection.CreateCommand();
            upsertCommand.Transaction = transaction;
            upsertCommand.CommandText = """
                INSERT INTO ops_metric_rollup (node_id, metric, step_seconds, bucket, first_value, last_value, min_value, max_value, avg_value, sample_count)
                VALUES (@nodeId, @metric, @stepSeconds, @bucket, @firstVal, @lastVal, @minVal, @maxVal, @avgVal, @count)
                ON CONFLICT (node_id, metric, step_seconds, bucket) DO UPDATE SET
                    first_value = excluded.first_value,
                    last_value = excluded.last_value,
                    min_value = excluded.min_value,
                    max_value = excluded.max_value,
                    avg_value = excluded.avg_value,
                    sample_count = excluded.sample_count
                WHERE ops_metric_rollup.first_value <> excluded.first_value
                   OR ops_metric_rollup.last_value <> excluded.last_value
                   OR ops_metric_rollup.min_value <> excluded.min_value
                   OR ops_metric_rollup.max_value <> excluded.max_value
                   OR ops_metric_rollup.avg_value <> excluded.avg_value
                   OR ops_metric_rollup.sample_count <> excluded.sample_count;
                """;
            Bind(
                upsertCommand,
                ("@nodeId", row.NodeId),
                ("@metric", row.Metric),
                ("@stepSeconds", stepSeconds),
                ("@bucket", row.Bucket),
                ("@firstVal", row.FirstVal),
                ("@lastVal", row.LastVal),
                ("@avgVal", row.AvgVal),
                ("@minVal", row.MinVal),
                ("@maxVal", row.MaxVal),
                ("@count", row.Count));

            written += await upsertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return written;
    }

    /// <summary>The newest bucket already rolled up for a step, or null when this database has none.</summary>
    private static async Task<long?> ReadLatestRollupBucketAsync(
        DbConnection connection,
        int stepSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(bucket) FROM ops_metric_rollup WHERE step_seconds = @stepSeconds;";
        Bind(command, ("@stepSeconds", stepSeconds));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads aggregated metric buckets for a time range.
    /// </summary>
    public async Task<IReadOnlyList<MetricRollup>> GetMetricRollupsAsync(
        string nodeId,
        string metric,
        int stepSeconds,
        long startBucket,
        long endBucket,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentException.ThrowIfNullOrEmpty(metric);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT node_id, metric, step_seconds, bucket, first_value, last_value, min_value, max_value, avg_value, sample_count
            FROM ops_metric_rollup
            WHERE node_id = @nodeId AND metric = @metric AND step_seconds = @stepSeconds AND bucket >= @startBucket AND bucket <= @endBucket
            ORDER BY bucket ASC;
            """;
        Bind(
            command,
            ("@nodeId", nodeId),
            ("@metric", metric),
            ("@stepSeconds", stepSeconds),
            ("@startBucket", startBucket),
            ("@endBucket", endBucket));

        var results = new List<MetricRollup>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new MetricRollup(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                reader.IsDBNull(5) ? 0 : reader.GetDouble(5),
                reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
                reader.IsDBNull(7) ? 0 : reader.GetDouble(7),
                reader.IsDBNull(8) ? 0 : reader.GetDouble(8),
                reader.GetInt32(9)));
        }

        return results;
    }
}
