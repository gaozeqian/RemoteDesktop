namespace RemoteDesktop.Server.Storage.UnitTests;

/// <summary>The contract, against the registry that keeps nothing. It is what a deployment without a database uses.</summary>
public sealed class InMemoryDeviceRegistryTests : DeviceRegistryContractTests
{
    protected override IDeviceRegistry CreateEmptyRegistry() => new InMemoryDeviceRegistry();
}
