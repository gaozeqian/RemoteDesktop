using RemoteDesktop.Server.Rendezvous;

namespace RemoteDesktop.Transport.IntegrationTests;

public class DeviceRegistryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Same_fingerprint_always_returns_the_same_device_id()
    {
        var registry = new InMemoryDeviceRegistry();
        const string fingerprint = "test-cert-sha256-fingerprint-123456";

        var first = await registry.GetOrAllocateAsync(fingerprint, Ct);
        var second = await registry.GetOrAllocateAsync(fingerprint, Ct);

        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.InRange(first.DeviceId, 100_000_000u, 999_999_999u);
    }

    /// <summary>
    /// The one thing the server cannot work out on its own: a device coming back is not a new device, and only the
    /// second call can say so.
    /// </summary>
    [Fact]
    public async Task The_call_that_allocates_the_id_is_the_only_one_that_says_it_created_it()
    {
        var registry = new InMemoryDeviceRegistry();

        var first = await registry.GetOrAllocateAsync("fingerprint-A", Ct);
        var again = await registry.GetOrAllocateAsync("fingerprint-A", Ct);

        Assert.True(first.Created);
        Assert.False(again.Created);
    }

    [Fact]
    public async Task Different_fingerprints_get_distinct_device_ids()
    {
        var registry = new InMemoryDeviceRegistry();

        var idA = await registry.GetOrAllocateAsync("fingerprint-A", Ct);
        var idB = await registry.GetOrAllocateAsync("fingerprint-B", Ct);

        Assert.NotEqual(idA.DeviceId, idB.DeviceId);
        Assert.True(idB.Created);
    }
}
