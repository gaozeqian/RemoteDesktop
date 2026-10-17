using Microsoft.Data.Sqlite;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// A console that is already running keeps its database. This is the one test that says so: it builds the schema a
/// previous version left behind, records its version, and checks that starting this build adds what it needs without
/// losing the row that was there.
/// </summary>
public class SchemaMigrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_version_one_database_gains_the_heartbeat_columns_and_keeps_its_sessions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ops-migration-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path}";
        try
        {
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(Ct);

                // The baseline as the previous build created it, a version-1 row, and one session that must survive.
                await using var create = connection.CreateCommand();
                create.CommandText = OpsSchema.Sql + """

                    INSERT INTO ops_schema_version (version, applied_at) VALUES (1, 0);
                    INSERT INTO ops_session (id, correlation_id, source, updated_at)
                    VALUES ('s1', 'c1', 'relay', 1);
                    """;
                await create.ExecuteNonQueryAsync(Ct);
            }

            using (var store = OpsStore.Open(new OpsDatabaseSettings { Provider = OpsDatabaseSettings.Sqlite, Path = path }))
            {
                await store.EnsureSchemaAsync(Ct);
            }

            await using var verify = new SqliteConnection(connectionString);
            await verify.OpenAsync(Ct);

            await using var columns = verify.CreateCommand();
            columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('ops_session') WHERE name IN ('platform', 'version', 'client_session_id');";
            Assert.Equal(3L, (long)(await columns.ExecuteScalarAsync(Ct))!);

            await using var versions = verify.CreateCommand();
            versions.CommandText = "SELECT MAX(version) FROM ops_schema_version;";
            Assert.Equal(3L, (long)(await versions.ExecuteScalarAsync(Ct))!);

            await using var row = verify.CreateCommand();
            row.CommandText = "SELECT source FROM ops_session WHERE id = 's1';";
            Assert.Equal("relay", (string?)await row.ExecuteScalarAsync(Ct));
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                File.Delete(path + suffix);
            }
        }
    }

    [Fact]
    public async Task Starting_twice_does_not_run_the_migration_twice()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ops-migration-{Guid.NewGuid():N}.db");
        try
        {
            var settings = new OpsDatabaseSettings { Provider = OpsDatabaseSettings.Sqlite, Path = path };

            using (var first = OpsStore.Open(settings))
            {
                await first.EnsureSchemaAsync(Ct);
            }

            // A second start would fail on ADD COLUMN if the version check were missing.
            using (var second = OpsStore.Open(settings))
            {
                await second.EnsureSchemaAsync(Ct);
            }
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                File.Delete(path + suffix);
            }
        }
    }
}
