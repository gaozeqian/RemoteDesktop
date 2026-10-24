using System.Security.Cryptography.X509Certificates;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Transport.Identity;

namespace RemoteDesktop.Core.UnitTests;

public class DeviceIdentityTests
{
    [Fact]
    public void The_device_key_can_be_handed_to_the_tls_stack()
    {
        using var identity = DeviceIdentity.CreateEphemeral();

        using var key = identity.Certificate.GetECDsaPrivateKey();
        Assert.NotNull(key);

        // This is what MsQuic does with the certificate when it builds its TLS configuration, and it is what fails
        // when the PKCS#12 is imported without X509KeyStorageFlags.Exportable: macOS then keeps the key in the keychain
        // as non-exportable and exporting it raises AppleCommonCryptoCryptographicException. The exception text
        // ("The contents of this item cannot be retrieved") names neither the key nor the flag.
        Assert.NotEmpty(key.ExportPkcs8PrivateKey());
    }

    [Fact]
    public void An_identity_is_stable_across_reloads()
    {
        var store = new InMemorySecretStore();

        using var first = DeviceIdentity.LoadOrCreate(store);
        using var second = DeviceIdentity.LoadOrCreate(store);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.Certificate.Thumbprint, second.Certificate.Thumbprint);
    }

    [Fact]
    public void A_controller_identity_is_separate_and_stable()
    {
        var store = new InMemorySecretStore();

        using var device = DeviceIdentity.LoadOrCreate(store);
        using var controller = DeviceIdentity.LoadOrCreate(store, DeviceIdentity.ControllerSecretName);
        using var controllerAgain = DeviceIdentity.LoadOrCreate(store, DeviceIdentity.ControllerSecretName);

        // A client beside the background service must not register with the service's device id: the signaling
        // server keeps one session per id, so the two would abort each other's connection.
        Assert.NotEqual(device.Fingerprint, controller.Fingerprint);
        Assert.Equal(controller.Fingerprint, controllerAgain.Fingerprint);
    }

    /// <summary>The smallest store that satisfies the port; the platform one is the subject of its own tests.</summary>
    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, byte[]> _secrets = [];

        public byte[]? Load(string name) => _secrets.TryGetValue(name, out var value) ? value : null;

        public void Save(string name, ReadOnlySpan<byte> secret) => _secrets[name] = secret.ToArray();

        public void Delete(string name) => _secrets.Remove(name);
    }
}
