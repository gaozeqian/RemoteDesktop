using RemoteDesktop.Server.Storage;

namespace RemoteDesktop.Server.Storage.UnitTests;

/// <summary>
/// The catalog's paging contract: ordered by id, exclusive cursor, and a limit that is honoured. The console leans on
/// all three to page millions of ids without an OFFSET.
/// </summary>
public class DeviceCatalogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pages_are_ordered_and_the_cursor_is_exclusive()
    {
        var registry = new InMemoryDeviceRegistry();
        var ids = new List<uint>();
        for (var index = 0; index < 5; index++)
        {
            var registration = await registry.GetOrAllocateAsync($"fingerprint-{index}", Ct);
            ids.Add(registration.DeviceId);
        }

        ids.Sort();

        var first = await registry.ListAsync(0, 2, Ct);
        Assert.Equal([ids[0], ids[1]], first.Select(record => record.DeviceId));

        var second = await registry.ListAsync(first[^1].DeviceId, 2, Ct);
        Assert.Equal([ids[2], ids[3]], second.Select(record => record.DeviceId));

        var third = await registry.ListAsync(second[^1].DeviceId, 2, Ct);
        Assert.Equal([ids[4]], third.Select(record => record.DeviceId));
    }

    [Fact]
    public async Task A_record_carries_the_times_the_device_page_shows()
    {
        var registry = new InMemoryDeviceRegistry();
        var registration = await registry.GetOrAllocateAsync("fingerprint", Ct);

        var record = Assert.Single(await registry.ListAsync(0, 10, Ct));

        Assert.Equal(registration.DeviceId, record.DeviceId);
        Assert.True(record.FirstSeenUnixMilliseconds > 0);
        Assert.True(record.LastSeenUnixMilliseconds >= record.FirstSeenUnixMilliseconds);
    }

    [Fact]
    public async Task An_empty_registry_returns_no_records_rather_than_throwing()
    {
        var registry = new InMemoryDeviceRegistry();

        Assert.Empty(await registry.ListAsync(0, 50, Ct));
    }
}
