using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Server.Storage;
using RemoteDesktop.TestSupport;

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
    /// The cache is an optimisation, not a registry: a public server meets a new device on every install, so it has
    /// to stay bounded. Dropping it must still answer from the database with the same id.
    /// </summary>
    [Fact]
    public async Task The_device_cache_is_bounded_and_falls_back_to_the_database()
    {
        var path = NewPath();
        using var registry = new SqliteDeviceRegistry(path, Logger);
        await registry.EnsureSchemaAsync(Ct);
        registry.CacheCapacity = 2;

        var ids = new List<uint>();
        for (var index = 0; index < 4; index++)
        {
            ids.Add((await registry.GetOrAllocateAsync($"fingerprint-{index}", Ct)).DeviceId);
            Assert.True(registry.CachedDeviceCount <= registry.CacheCapacity);
        }

        var again = await registry.GetOrAllocateAsync("fingerprint-0", Ct);
        Assert.Equal(ids[0], again.DeviceId);
        Assert.False(again.Created);
    }

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

    /// <summary>
    /// "Last seen" is the last registration. The cache answers a reconnect without a query, and used to answer without
    /// refreshing the timestamp too, so a device that reconnected every day showed the day it first registered.
    /// </summary>
    [Fact]
    public async Task A_cached_device_that_registers_again_refreshes_last_seen_once_the_throttle_window_has_passed()
    {
        var time = new TestClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        using var registry = new SqliteDeviceRegistry(NewPath(), Logger, allocateId: null, time);
        await registry.EnsureSchemaAsync(Ct);

        var first = await registry.GetOrAllocateAsync("fingerprint-A", Ct);
        var created = Assert.Single(await registry.ListAsync(0, 10, Ct)).LastSeenUnixMilliseconds;
        Assert.Equal(time.GetUtcNow().ToUnixTimeMilliseconds(), created);

        time.Advance(TimeSpan.FromMinutes(5));
        var again = await registry.GetOrAllocateAsync("fingerprint-A", Ct);
        Assert.Equal(first.DeviceId, again.DeviceId);
        Assert.False(again.Created);

        // The touch is fire-and-forget, so wait for it rather than assume it has landed.
        var expected = time.GetUtcNow().ToUnixTimeMilliseconds();
        Assert.Equal(expected, await WaitForLastSeenAsync(registry, expected));
        Assert.True(expected > created);
    }

    /// <summary>
    /// Without a throttle a reconnect storm is a write per connection, and on PostgreSQL those writes are unbounded
    /// background tasks. Inside the window the timestamp is left alone.
    /// </summary>
    [Fact]
    public async Task A_device_that_registers_again_inside_the_throttle_window_does_not_cause_a_write()
    {
        var time = new TestClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        using var registry = new SqliteDeviceRegistry(NewPath(), Logger, allocateId: null, time);
        await registry.EnsureSchemaAsync(Ct);

        await registry.GetOrAllocateAsync("fingerprint-A", Ct);
        var created = Assert.Single(await registry.ListAsync(0, 10, Ct)).LastSeenUnixMilliseconds;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            await registry.GetOrAllocateAsync("fingerprint-A", Ct);
        }

        // 50 seconds in: a write would have changed the stamp, and has had time to land if it was going to.
        await Task.Delay(500, Ct);
        Assert.Equal(created, Assert.Single(await registry.ListAsync(0, 10, Ct)).LastSeenUnixMilliseconds);

        // One more step passes the window, and the very next registration is written.
        time.Advance(TimeSpan.FromSeconds(11));
        await registry.GetOrAllocateAsync("fingerprint-A", Ct);
        var expected = time.GetUtcNow().ToUnixTimeMilliseconds();
        Assert.Equal(expected, await WaitForLastSeenAsync(registry, expected));
    }

    [Fact]
    public async Task The_throttle_is_per_device()
    {
        var time = new TestClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        using var registry = new SqliteDeviceRegistry(NewPath(), Logger, allocateId: null, time);
        await registry.EnsureSchemaAsync(Ct);

        await registry.GetOrAllocateAsync("fingerprint-A", Ct);
        time.Advance(TimeSpan.FromSeconds(30));
        await registry.GetOrAllocateAsync("fingerprint-B", Ct);
        time.Advance(TimeSpan.FromSeconds(31));

        // A was last written 61 seconds ago, B 31: only A is due.
        await registry.GetOrAllocateAsync("fingerprint-A", Ct);
        await registry.GetOrAllocateAsync("fingerprint-B", Ct);

        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var bWritten = now - 31_000;
        await WaitForAsync(registry, records => records.Any(record => record.LastSeenUnixMilliseconds == now));
        await Task.Delay(300, Ct);

        var records = await registry.ListAsync(0, 10, Ct);
        Assert.Equal(1, records.Count(record => record.LastSeenUnixMilliseconds == now));
        Assert.Equal(1, records.Count(record => record.LastSeenUnixMilliseconds == bWritten));
    }

    private static async Task<long> WaitForLastSeenAsync(SqliteDeviceRegistry registry, long expected)
    {
        var records = await WaitForAsync(registry, list => list.Count == 1 && list[0].LastSeenUnixMilliseconds == expected);
        return records[0].LastSeenUnixMilliseconds;
    }

    /// <summary>Polls the catalog until the condition holds, or returns the last page after a few seconds.</summary>
    private static async Task<IReadOnlyList<DeviceRecord>> WaitForAsync(
        SqliteDeviceRegistry registry,
        Func<IReadOnlyList<DeviceRecord>, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        IReadOnlyList<DeviceRecord> records;
        do
        {
            records = await registry.ListAsync(0, 10, Ct);
            if (condition(records))
            {
                break;
            }

            await Task.Delay(25, Ct);
        }
        while (DateTime.UtcNow < deadline);

        return records;
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
