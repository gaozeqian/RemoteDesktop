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

        var id1 = await registry.GetOrAllocateAsync(fingerprint, Ct);
        var id2 = await registry.GetOrAllocateAsync(fingerprint, Ct);

        Assert.Equal(id1, id2);
        Assert.InRange(id1, 100_000_000u, 999_999_999u);
    }

    [Fact]
    public async Task Different_fingerprints_get_distinct_device_ids()
    {
        var registry = new InMemoryDeviceRegistry();

        var idA = await registry.GetOrAllocateAsync("fingerprint-A", Ct);
        var idB = await registry.GetOrAllocateAsync("fingerprint-B", Ct);

        Assert.NotEqual(idA, idB);
    }
}
