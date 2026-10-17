using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace RemoteDesktop.Server.Storage;

/// <summary>
/// The device registry over SQLite, for a single machine that should not have to run a database server.
/// </summary>
/// <remarks>
/// Three things are specific to this store and are all decided here rather than in the shared algorithm:
/// the database runs in WAL mode so that reads never wait for the one writer; writes are serialised in-process,
/// because SQLite allows only one at a time; and the id column carries a <c>CHECK</c>, because SQLite's typing is
/// per-value and would otherwise accept text in a column the rest of the code reads as a number.
/// </remarks>
public sealed class SqliteDeviceRegistry : RelationalDeviceRegistry
{
    /// <summary><c>SQLITE_CONSTRAINT_UNIQUE</c>. <c>SQLITE_CONSTRAINT_PRIMARYKEY</c> (1555) cannot reach the retry:
    /// the upsert names the fingerprint as its conflict target, so that collision is absorbed by the statement.</summary>
    private const int UniqueConstraint = 2067;

    public SqliteDeviceRegistry(string path, ILogger<SqliteDeviceRegistry> logger)
        : this(path, logger, allocateId: null)
    {
    }

    /// <param name="allocateId">
    /// Overrides the random nine-digit draw. Only the tests pass one: the collision retry is otherwise a
    /// one-in-a-billion path that no test could reach.
    /// </param>
    internal SqliteDeviceRegistry(string path, ILogger<SqliteDeviceRegistry> logger, Func<uint>? allocateId)
        : base(new SqliteDataSource(ConnectionStringFor(path)), new SqliteDialect(), logger, allocateId)
    {
    }

    /// <summary>The connection string for a database file. <c>Default Timeout</c> is the window a command retries
    /// for while another connection holds the write lock, which is what keeps a burst of reconnects from failing.</summary>
    internal static string ConnectionStringFor(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5,
            Pooling = true,
        }.ToString();
    }

    /// <summary>
    /// SQLite has no <see cref="DbDataSource"/> of its own; this is the adapter the shared algorithm opens
    /// connections through. Pooling is the driver's, keyed by the connection string.
    /// </summary>
    private sealed class SqliteDataSource(string connectionString) : DbDataSource
    {
        public override string ConnectionString { get; } = connectionString;

        protected override DbConnection CreateDbConnection() => new SqliteConnection(ConnectionString);
    }

    private sealed class SqliteDialect : IDeviceRegistryDialect
    {
        public string CreateSchemaSql =>
            """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS device_registrations (
                fingerprint TEXT PRIMARY KEY,
                device_id INTEGER NOT NULL UNIQUE CHECK (device_id BETWEEN 100000000 AND 999999999),
                created_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                revoked_at TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_device_registrations_device_id ON device_registrations(device_id);
            """;

        public string AddRevokedAtSql =>
            "ALTER TABLE device_registrations ADD COLUMN revoked_at TEXT;";

        public string SelectDeviceIdSql =>
            "SELECT device_id FROM device_registrations WHERE fingerprint = @fingerprint;";

        public string InsertIfAbsentSql =>
            """
            INSERT INTO device_registrations (fingerprint, device_id, created_at, last_seen_at)
            VALUES (@fingerprint, @device_id, @now, @now)
            ON CONFLICT (fingerprint) DO NOTHING;
            """;

        public string TouchLastSeenSql =>
            "UPDATE device_registrations SET last_seen_at = @now WHERE fingerprint = @fingerprint;";

        public string SetRevokedSql =>
            "UPDATE device_registrations SET revoked_at = @revoked WHERE device_id = @device_id;";

        public string IsRevokedSql =>
            "SELECT revoked_at IS NOT NULL FROM device_registrations WHERE device_id = @device_id;";

        public bool SerializeWrites => true;

        public bool IsUniqueViolation(Exception exception) =>
            exception is SqliteException { SqliteExtendedErrorCode: UniqueConstraint };

        public bool IsDuplicateColumn(Exception exception) =>
            exception is SqliteException { Message: var message } && message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase);
    }
}
