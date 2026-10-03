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
    public static IServiceCollection AddQuicTransport(this IServiceCollection services, TransportOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton(sp => DeviceIdentity.LoadOrCreate(sp.GetRequiredService<ISecretStore>()));
        services.AddSingleton<IRendezvousClient, RendezvousClient>();
        services.AddSingleton<IPeerConnector, QuicPeerConnector>();
        services.AddSingleton<IPeerListener, QuicPeerListener>();
        return services;
    }
}
