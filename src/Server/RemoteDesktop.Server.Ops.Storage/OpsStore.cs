using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.Storage;

/// <summary>
/// Everything the console keeps. One type rather than a repository per table: the console's read patterns are a
/// handful of small queries, and five interfaces with one implementation each would be five places to look for the
/// only caller.
/// </summary>
/// <remarks>
/// A store holds no open connection. Both drivers pool underneath, and a long-lived connection here would be a lock
/// held across requests — the exact thing SQLite handles worst.
/// </remarks>
public sealed partial class OpsStore : IDisposable
{
    private readonly string _provider;
    private readonly string _connectionString;

    /// <summary>Serialises audit appends so the chain cannot fork. See the remarks on <see cref="AppendAuditAsync"/>.</summary>
    private readonly SemaphoreSlim _auditGate = new(1, 1);

    /// <summary>
    /// Serialises ingest batches. Two batches for one correlation id each read the session, merge their events and
    /// write the whole row; interleaved, the second overwrites the first's fields and the projection silently loses an
    /// update. The console is a single process, so one gate is enough, and ingest batches are small and infrequent.
    /// </summary>
    private readonly SemaphoreSlim _ingestGate = new(1, 1);

    private OpsStore(string provider, string connectionString)
    {
        _provider = provider;
        _connectionString = connectionString;
    }

    /// <summary>The provider in canonical spelling, for logging.</summary>
    public string Provider => _provider;

    /// <summary>Releases the in-process gates. Connections are pooled by the driver and are not this type's to own.</summary>
    public void Dispose()
    {
        _auditGate.Dispose();
        _ingestGate.Dispose();
    }

    /// <summary>
    /// A store for <paramref name="settings"/>, with the SQLite folder created if it is missing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The settings cannot work; the message names the key.</exception>
    public static OpsStore Open(OpsDatabaseSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        var provider = OpsDatabaseSettings.Canonical(settings.Provider)
            ?? throw new InvalidOperationException($"Ops:Database:Provider is not usable: '{settings.Provider}'.");

        if (string.Equals(provider, OpsDatabaseSettings.Sqlite, StringComparison.Ordinal))
        {
            var path = Path.GetFullPath(settings.Path!);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
            return new OpsStore(provider, connectionString);
        }

        return new OpsStore(provider, settings.ConnectionString!);
    }

    /// <summary>Creates the baseline schema if it is not there. Run once at start-up; a failure stops the host.</summary>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);

        if (string.Equals(_provider, OpsDatabaseSettings.Sqlite, StringComparison.Ordinal))
        {
            // WAL keeps the scraper's writes from blocking a page read. NORMAL trades a crash-window of the last
            // transaction for not fsyncing every batch — acceptable for telemetry, and the default is far slower.
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = OpsSchema.Sql;
            await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Everything above is the baseline, which a fresh database and an old one both have. What follows only runs
        // on a database that has not seen it yet, one transaction per step, so a half-applied migration is not left
        // behind for the next start-up to trip over.
        var applied = await ReadSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        foreach (var migration in OpsMigrations.All.Where(migration => migration.Version > applied).OrderBy(migration => migration.Version))
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (var step = connection.CreateCommand())
            {
                step.Transaction = transaction;
                step.CommandText = string.Equals(_provider, OpsDatabaseSettings.Postgres, StringComparison.Ordinal)
                    && migration.PostgresSql is { } postgres
                        ? postgres
                        : migration.Sql;
                await step.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = "INSERT INTO ops_schema_version (version, applied_at) VALUES (@version, @appliedAt);";
                Bind(record, ("@version", migration.Version), ("@appliedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var version = connection.CreateCommand();
        version.CommandText =
            "INSERT INTO ops_schema_version (version, applied_at) VALUES (@version, @appliedAt) ON CONFLICT (version) DO NOTHING;";
        Bind(version, ("@version", OpsSchema.Version), ("@appliedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await version.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The highest schema version this database has recorded; 0 when it has none.</summary>
    private static async Task<int> ReadSchemaVersionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM ops_schema_version;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Whether <paramref name="exception"/> is the database refusing a row because a unique key already holds it, on
    /// either provider. Lets a caller turn "someone else inserted that name first" into a conflict instead of an error.
    /// </summary>
    public static bool IsUniqueViolation(Exception exception) =>
        exception switch
        {
            // 2067 is SQLITE_CONSTRAINT_UNIQUE and 1555 is SQLITE_CONSTRAINT_PRIMARYKEY.
            SqliteException { SqliteExtendedErrorCode: 2067 or 1555 } => true,
            PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } => true,
            _ => false,
        };

    /// <summary>Throws when the database cannot answer. The health endpoint turns that into an unhealthy report.</summary>
    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1;";
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A connection to the store's database, open. The caller disposes it.
    /// </summary>
    /// <remarks>
    /// The <c>finally</c> is what disposes a connection whose open failed, without catching the exception — a bare
    /// catch here would swallow nothing but would still have to be filtered and rethrown, and the flag says exactly
    /// which outcome needs the cleanup.
    /// </remarks>
    internal async Task<DbConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        DbConnection connection = string.Equals(_provider, OpsDatabaseSettings.Sqlite, StringComparison.Ordinal)
            ? new SqliteConnection(_connectionString)
            : new NpgsqlConnection(_connectionString);

        var opened = false;
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            opened = true;
            return connection;
        }
        finally
        {
            if (!opened)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Adds one named parameter. Values are <c>DBNull</c> rather than <c>null</c>, which ADO.NET treats as "no value".</summary>
    internal static void Bind(DbCommand command, params (string Name, object? Value)[] values)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(values);

        foreach (var (name, value) in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }
}
