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

    // The corrupt-file recovery is covered in Platform.Windows.UnitTests/RecentDevicesTests, which injects the same
    // unreadable file and asserts a superset: exactly one .corrupt- copy, the original bytes left untouched, and the
    // store usable afterwards — alongside four other malformed shapes. A second, weaker copy lived here until it was
    // removed; what is unique to this file is the concurrent-write case above.

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, byte[]> _secrets = [];

        public void Save(string name, ReadOnlySpan<byte> secret) => _secrets[name] = secret.ToArray();

        public byte[]? Load(string name) => _secrets.TryGetValue(name, out var value) ? value : null;

        public void Delete(string name) => _secrets.Remove(name);
    }
}
