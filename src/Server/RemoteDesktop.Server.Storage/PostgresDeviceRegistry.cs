using Microsoft.Extensions.Logging;
using Npgsql;

namespace RemoteDesktop.Server.Storage;

/// <summary>
/// The device registry over PostgreSQL. Ids are kept in <c>device_registrations</c>; the schema is created on
/// start-up and is the same one earlier versions of this server created, so an existing deployment needs no migration.
/// </summary>
public sealed class PostgresDeviceRegistry : RelationalDeviceRegistry
{
    public PostgresDeviceRegistry(string connectionString, ILogger<PostgresDeviceRegistry> logger)
        : base(DataSource(connectionString), new PostgresDialect(), logger)
    {
    }

    private static NpgsqlDataSource DataSource(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new NpgsqlDataSourceBuilder(connectionString).Build();
    }

    /// <summary>
    /// PostgreSQL's half of the dialect's seven statements. The upsert names the fingerprint as the conflict target, so a
    /// collision on the device id itself still raises a unique violation and is retried by the base class.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so that a test can read the statements: there is no PostgreSQL in CI, and the
    /// schema is the one thing here that must not drift — every installed deployment already has this table, and a
    /// changed statement would only show up as a failure on somebody's server.
    /// </remarks>
    internal sealed class PostgresDialect : IDeviceRegistryDialect
    {
        public string CreateSchemaSql =>
            """
            CREATE TABLE IF NOT EXISTS device_registrations (
                fingerprint VARCHAR(128) PRIMARY KEY,
                device_id BIGINT UNIQUE NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                last_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                revoked_at TIMESTAMPTZ
            );
            CREATE INDEX IF NOT EXISTS idx_device_registrations_device_id ON device_registrations(device_id);
            """;

        public string AddRevokedAtSql =>
            "ALTER TABLE device_registrations ADD COLUMN IF NOT EXISTS revoked_at TIMESTAMPTZ;";

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

        public bool SerializeWrites => false;

        public bool IsUniqueViolation(Exception exception) =>
            exception is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

        public bool IsDuplicateColumn(Exception exception) => false;
    }
}
