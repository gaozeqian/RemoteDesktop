using RemoteDesktop.Server.Storage;

namespace RemoteDesktop.Server.Storage.UnitTests;

/// <summary>
/// The PostgreSQL statements, read as text because CI has no PostgreSQL to run them against.
/// </summary>
/// <remarks>
/// This is a drift guard, not a behaviour test. Every installed deployment already has this table, so the schema is
/// not something to improve in passing: a changed column type or a dropped index would first be noticed by a server
/// that stopped starting. The other three statements are covered by the shared contract when the store is SQLite,
/// which speaks the same shape.
/// </remarks>
public class PostgresDialectTests
{
    private static readonly PostgresDeviceRegistry.PostgresDialect Dialect = new();

    [Fact]
    public void The_schema_is_the_one_earlier_versions_created()
    {
        var sql = Dialect.CreateSchemaSql;

        Assert.Contains("CREATE TABLE IF NOT EXISTS device_registrations", sql, StringComparison.Ordinal);
        Assert.Contains("fingerprint VARCHAR(128) PRIMARY KEY", sql, StringComparison.Ordinal);
        Assert.Contains("device_id BIGINT UNIQUE NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()", sql, StringComparison.Ordinal);
        Assert.Contains("last_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW()", sql, StringComparison.Ordinal);
        Assert.Contains("idx_device_registrations_device_id", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The conflict target is the fingerprint and nothing else. Naming the device id here would swallow the collision
    /// instead of raising it, and the retry that exists for it would never run.
    /// </summary>
    [Fact]
    public void The_insert_lets_a_device_id_collision_through_to_the_retry()
    {
        var sql = Dialect.InsertIfAbsentSql;

        Assert.Contains("ON CONFLICT (fingerprint) DO NOTHING", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("RETURNING", sql, StringComparison.OrdinalIgnoreCase);
    }
}
