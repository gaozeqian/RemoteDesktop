using RemoteDesktop.Server.Storage;

namespace RemoteDesktop.Server.Storage.UnitTests;

/// <summary>
/// What every device registry promises, whatever it keeps its ids in.
/// </summary>
/// <remarks>
/// The contract lives here rather than beside one implementation because it is the thing a fourth database has to
/// satisfy: a new provider derives from this class and gets every case below without writing one. That is also why
/// nothing here may reach for a type that only one store has.
/// </remarks>
public abstract class DeviceRegistryContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A registry with nothing in it, on storage of its own. Each call has to hand back a fresh one: the cases below
    /// must not see each other's fingerprints.
    /// </summary>
    protected abstract IDeviceRegistry CreateEmptyRegistry();

    /// <summary>
    /// The registry, with its table created if it has one. A store's schema is created by the host at start-up; here
    /// this is the host.
    /// </summary>
    private async Task<IDeviceRegistry> CreateRegistryAsync()
    {
        var registry = CreateEmptyRegistry();
        if (registry is IDeviceRegistryStore store)
        {
            await store.EnsureSchemaAsync(Ct);
        }

        return registry;
    }

    [Fact]
    public async Task Same_fingerprint_always_returns_the_same_device_id()
    {
        var registry = await CreateRegistryAsync();
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
        var registry = await CreateRegistryAsync();

        var first = await registry.GetOrAllocateAsync("fingerprint-A", Ct);
        var again = await registry.GetOrAllocateAsync("fingerprint-A", Ct);

        Assert.True(first.Created);
        Assert.False(again.Created);
    }

    [Fact]
    public async Task Different_fingerprints_get_distinct_device_ids()
    {
        var registry = await CreateRegistryAsync();

        var idA = await registry.GetOrAllocateAsync("fingerprint-A", Ct);
        var idB = await registry.GetOrAllocateAsync("fingerprint-B", Ct);

        Assert.NotEqual(idA.DeviceId, idB.DeviceId);
        Assert.True(idB.Created);
    }

    /// <summary>
    /// Two connections for the same machine arriving at once is not an edge case — a host reconnecting while the
    /// client also reconnects does it. Exactly one of them created the id; the rest must be told the same one.
    /// </summary>
    [Fact]
    public async Task Concurrent_calls_for_one_fingerprint_agree_on_the_id()
    {
        var registry = await CreateRegistryAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => registry.GetOrAllocateAsync("fingerprint-same", Ct)));

        Assert.Single(results.Select(result => result.DeviceId).Distinct());
        Assert.Single(results, result => result.Created);
    }

    /// <summary>Thirty-two devices coming online together — the shape of an office at nine in the morning.</summary>
    [Fact]
    public async Task Concurrent_registrations_do_not_share_an_id()
    {
        var registry = await CreateRegistryAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(index => registry.GetOrAllocateAsync($"fingerprint-{index}", Ct)));

        Assert.Equal(32, results.Select(result => result.DeviceId).Distinct().Count());
        Assert.All(results, result => Assert.True(result.Created));
    }
}
