using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Server.Storage;

namespace RemoteDesktop.Server.Storage.UnitTests;

/// <summary>
/// Revocation is the one mutation an operator can make to the device registry, and the whole point of it is that it
/// outlives a restart and shows up in the device list. Both are what these tests pin down.
/// </summary>
public sealed class DeviceRevocationTests : IDisposable
{
    private static readonly ILogger<SqliteDeviceRegistry> Logger = NullLogger<SqliteDeviceRegistry>.Instance;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rd-revoke-tests", Guid.NewGuid().ToString("N"));

    public DeviceRevocationTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Revocation_survives_a_restart_and_appears_in_the_list()
    {
        var path = Path.Combine(_folder, "revoke.db");

        uint deviceId;
        using (var first = new SqliteDeviceRegistry(path, Logger))
        {
            await first.EnsureSchemaAsync(Ct);
            deviceId = (await first.GetOrAllocateAsync("fingerprint-A", Ct)).DeviceId;
            await first.SetRevokedAsync(deviceId, revoked: true, Ct);
        }

        using var second = new SqliteDeviceRegistry(path, Logger);
        Assert.True(await second.IsRevokedAsync(deviceId, Ct));
        var record = Assert.Single(await second.ListAsync(0, 10, Ct));
        Assert.True(record.Revoked);

        await second.SetRevokedAsync(deviceId, revoked: false, Ct);
        Assert.False(await second.IsRevokedAsync(deviceId, Ct));
    }

    [Fact]
    public async Task A_table_from_before_revocation_gains_the_column()
    {
        var path = Path.Combine(_folder, "old.db");
        var connectionString = SqliteDeviceRegistry.ConnectionStringFor(path);

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE device_registrations (
                    fingerprint TEXT PRIMARY KEY,
                    device_id INTEGER NOT NULL UNIQUE,
                    created_at TEXT NOT NULL,
                    last_seen_at TEXT NOT NULL
                );
                """;
            await create.ExecuteNonQueryAsync(Ct);
        }

        using var registry = new SqliteDeviceRegistry(path, Logger);
        await registry.EnsureSchemaAsync(Ct);
        await registry.EnsureSchemaAsync(Ct); // the second start must not trip over the column it already added

        var deviceId = (await registry.GetOrAllocateAsync("fingerprint-old", Ct)).DeviceId;
        await registry.SetRevokedAsync(deviceId, revoked: true, Ct);
        Assert.True(await registry.IsRevokedAsync(deviceId, Ct));
    }

    [Fact]
    public async Task The_in_memory_registry_revokes_and_un_revokes()
    {
        var registry = new InMemoryDeviceRegistry();
        var deviceId = (await registry.GetOrAllocateAsync("fingerprint", Ct)).DeviceId;

        Assert.False(await registry.IsRevokedAsync(deviceId, Ct));
        await registry.SetRevokedAsync(deviceId, revoked: true, Ct);
        Assert.True(await registry.IsRevokedAsync(deviceId, Ct));
        Assert.True(Assert.Single(await registry.ListAsync(0, 10, Ct)).Revoked);

        await registry.SetRevokedAsync(deviceId, revoked: false, Ct);
        Assert.False(await registry.IsRevokedAsync(deviceId, Ct));
    }
}
