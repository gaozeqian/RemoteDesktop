using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Server.Storage;

namespace RemoteDesktop.Server.Storage.UnitTests;

/// <summary>
/// The contract plus the things only a file-backed store can get wrong: ids surviving a restart, the file being
/// deleted, the id column's range, and the retry when two devices draw the same id.
/// </summary>
public sealed class SqliteDeviceRegistryTests : DeviceRegistryContractTests, IDisposable
{
    private static readonly ILogger<SqliteDeviceRegistry> Logger = NullLogger<SqliteDeviceRegistry>.Instance;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rd-storage-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SqliteDeviceRegistryTests() => Directory.CreateDirectory(_folder);

    protected override IDeviceRegistry CreateEmptyRegistry() => new SqliteDeviceRegistry(NewPath(), Logger);

    /// <summary>
    /// The point of a file: a new process — here, a new registry with an empty cache — finds the same id. Without
    /// this, every restart of a single-machine server would reissue every device code.
    /// </summary>
    [Fact]
    public async Task Ids_survive_a_new_registry_instance()
    {
        var path = NewPath();

        uint allocated;
        using (var first = new SqliteDeviceRegistry(path, Logger))
        {
            await first.EnsureSchemaAsync(Ct);
            allocated = (await first.GetOrAllocateAsync("fingerprint-A", Ct)).DeviceId;
        }

        using var second = new SqliteDeviceRegistry(path, Logger);
        var again = await second.GetOrAllocateAsync("fingerprint-A", Ct);

        Assert.Equal(allocated, again.DeviceId);
        Assert.False(again.Created);
    }

    /// <summary>WAL is what lets a reader answer while the one writer holds the lock; it is a property of the file,
    /// so it is set once at schema creation and survives every later connection.</summary>
    [Fact]
    public async Task The_database_runs_in_wal_mode()
    {
        var path = NewPath();
        using var registry = new SqliteDeviceRegistry(path, Logger);
        await registry.EnsureSchemaAsync(Ct);

        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";

        Assert.Equal("wal", Convert.ToString(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The failure that a health check exists for. SQLite creates an empty database when the file it is pointed at is
    /// missing, so a probe that only asked the connection whether it works would answer "healthy" to a server that has
    /// just forgotten every device code. Asking the table is what makes the difference.
    /// </summary>
    [Fact]
    public async Task A_deleted_database_file_is_reported_as_unreachable()
    {
        var path = NewPath();
        using var registry = new SqliteDeviceRegistry(path, Logger);
        await registry.EnsureSchemaAsync(Ct);
        await registry.ProbeAsync(Ct);

        SqliteConnection.ClearAllPools();
        File.Delete(path);

        await Assert.ThrowsAnyAsync<DbException>(() => registry.ProbeAsync(Ct));
    }

    /// <summary>
    /// SQLite types a value, not a column, so nothing but this constraint stops a text or out-of-range id from being
    /// stored — and the read path casts what it finds to a number.
    /// </summary>
    [Fact]
    public async Task The_schema_refuses_an_id_outside_the_nine_digit_range()
    {
        var path = NewPath();
        using var registry = new SqliteDeviceRegistry(path, Logger);
        await registry.EnsureSchemaAsync(Ct);

        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO device_registrations (fingerprint, device_id, created_at, last_seen_at) VALUES ('too-small', 42, 'now', 'now');";

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync(Ct));
    }

    /// <summary>
    /// Two devices drawing the same id is a one-in-a-billion event, which is exactly why it would never be noticed if
    /// the retry were wrong. The allocator is handed in so the draw can be forced.
    /// </summary>
    [Fact]
    public async Task A_colliding_device_id_is_retried_with_another_draw()
    {
        const uint Taken = 123_456_789;
        const uint Free = 987_654_321;

        var path = NewPath();
        using (var seed = new SqliteDeviceRegistry(path, Logger, () => Taken))
        {
            await seed.EnsureSchemaAsync(Ct);
            Assert.Equal(Taken, (await seed.GetOrAllocateAsync("fingerprint-A", Ct)).DeviceId);
        }

        var draws = new Queue<uint>([Taken, Free]);
        using var registry = new SqliteDeviceRegistry(path, Logger, () => draws.Dequeue());

        var second = await registry.GetOrAllocateAsync("fingerprint-B", Ct);

        Assert.Equal(Free, second.DeviceId);
        Assert.True(second.Created);
    }

    private string NewPath() => Path.Combine(_folder, $"{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        // The driver pools connections by connection string; without this the files are still open and Windows
        // refuses to delete them.
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is not a test failure.
        }

        GC.SuppressFinalize(this);
    }
}
