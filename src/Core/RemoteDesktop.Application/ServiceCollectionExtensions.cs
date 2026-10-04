using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Agent;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Application;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Session use cases. The host composition must also register a transport (e.g. <c>AddQuicTransport</c>).
    /// </summary>
    public static IServiceCollection AddRemoteDesktopApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ChannelDispatcher>();
        services.AddSingleton<HostCredentials>();
        services.AddSingleton<AuthAttemptLimiter>();
        services.AddSingleton<ControllerSessionService>();
        services.AddSingleton<HostSessionService>();
        services.AddHostedService<TransportWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<HostSessionService>());
        return services;
    }

    /// <summary>
    /// Serves the desktop channels (screen and input) through the session agent instead of in this process. Used by
    /// the background service, which cannot touch the interactive desktop itself. Requires an <see cref="IAgentGateway"/>.
    /// </summary>
    public static IServiceCollection AddAgentForwardedDesktop(this IServiceCollection services)
    {
        services.AddSingleton<IFeatureModule>(sp => new AgentForwardingModule(
            ChannelType.Video, Permissions.ViewScreen, AgentChannelKind.Video, replayFirstFrame: true,
            sp.GetRequiredService<IAgentGateway>(), sp.GetRequiredService<ILogger<AgentForwardingModule>>()));
        services.AddSingleton<IFeatureModule>(sp => new AgentForwardingModule(
            ChannelType.Input, Permissions.ControlInput, AgentChannelKind.Input, replayFirstFrame: false,
            sp.GetRequiredService<IAgentGateway>(), sp.GetRequiredService<ILogger<AgentForwardingModule>>()));
        return services;
    }

    /// <summary>Lets the UI (or the service's control pipe) read and change this process's host state.</summary>
    /// <param name="isService">True in the background service, so clients can tell where the host lives.</param>
    public static IServiceCollection AddLocalHostControl(this IServiceCollection services, bool isService)
    {
        services.AddSingleton<IHostControl>(sp => new LocalHostControl(
            sp.GetRequiredService<IRendezvousClient>(),
            sp.GetRequiredService<HostCredentials>(),
            sp.GetRequiredService<HostSessionService>(),
            sp.GetService<IAgentGateway>(),
            isService));
        return services;
    }
}
