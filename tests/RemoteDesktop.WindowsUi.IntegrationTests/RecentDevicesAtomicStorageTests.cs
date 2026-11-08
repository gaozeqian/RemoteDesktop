using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Client.Storage;

namespace RemoteDesktop.WindowsUi.IntegrationTests;

public sealed class RecentDevicesAtomicStorageTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"rd_atomic_test_{Guid.NewGuid():N}");

    public RecentDevicesAtomicStorageTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task RecentDevicesStore_concurrent_saves_preserve_all_updates_without_corruption()
    {
        using var store = new RecentDevicesStore(_tempDir, new MemorySecrets());
        var ct = TestContext.Current.CancellationToken;

        // Fire 10 concurrent writes
        var tasks = Enumerable.Range(1, 10).Select(i =>
            Task.Run(async () =>
            {
                var id = $"100 000 00{i:D2}";
                await store.SaveOrUpdateAsync(id, alias: $"Device-{i}", cancellationToken: ct);
            }, ct)).ToArray();

        await Task.WhenAll(tasks);

        var list = await store.GetAllAsync(ct);
        Assert.Equal(10, list.Count);
        for (var i = 1; i <= 10; i++)
        {
            var id = $"100 000 00{i:D2}";
            Assert.Contains(list, e => e.DeviceId == id && e.Alias == $"Device-{i}");
        }
    }

    [Fact]
    public async Task RecentDevicesStore_backs_up_corrupted_file_and_recovers_cleanly()
    {
        var file = Path.Combine(_tempDir, "recent_devices.json");
        await File.WriteAllTextAsync(file, "{ unparseable invalid json bytes content ...", TestContext.Current.CancellationToken);

        using var store = new RecentDevicesStore(_tempDir, new MemorySecrets());
        var ct = TestContext.Current.CancellationToken;

        await store.SaveOrUpdateAsync("999 888 777", alias: "RecoveryDev", cancellationToken: ct);

        // The corrupt file was moved out of the way to a .corrupt- timestamp file
        var backups = Directory.GetFiles(_tempDir, "recent_devices.json.corrupt-*");
        Assert.NotEmpty(backups);

        var list = await store.GetAllAsync(ct);
        var entry = Assert.Single(list);
        Assert.Equal("999 888 777", entry.DeviceId);
        Assert.Equal("RecoveryDev", entry.Alias);
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, byte[]> _secrets = [];

        public void Save(string name, ReadOnlySpan<byte> secret) => _secrets[name] = secret.ToArray();

        public byte[]? Load(string name) => _secrets.TryGetValue(name, out var value) ? value : null;

        public void Delete(string name) => _secrets.Remove(name);
    }
}
