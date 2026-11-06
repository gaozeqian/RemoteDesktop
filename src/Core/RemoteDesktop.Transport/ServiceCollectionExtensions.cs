using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Transport.Identity;
using RemoteDesktop.Transport.Quic;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Transport;

public static class ServiceCollectionExtensions
{
    /// <summary>QUIC peer transport with rendezvous signaling. Requires an <see cref="ISecretStore"/> for the device identity.</summary>
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    /// <param name="identitySecretName">
    /// Where the device's key is kept. A client running beside a background service names its own, so the two
    /// processes do not register under the same device id; see <see cref="DeviceIdentity.ControllerSecretName"/>.
    /// </param>
    public static IServiceCollection AddQuicTransport(
        this IServiceCollection services,
        TransportOptions options,
        string identitySecretName = DeviceIdentity.SecretName)
    {
        services.AddSingleton(options);
        services.AddSingleton(sp => DeviceIdentity.LoadOrCreate(sp.GetRequiredService<ISecretStore>(), identitySecretName));
        services.AddSingleton<IRendezvousClient, RendezvousClient>();
        services.AddSingleton<IPeerConnector, QuicPeerConnector>();
        services.AddSingleton<IPeerListener, QuicPeerListener>();
        return services;
    }
}
