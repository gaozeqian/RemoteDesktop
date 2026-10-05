using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Agent;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;
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
        services.TryAddSingleton<ConnectionAdmission>();
        services.AddSingleton<ChannelDispatcher>();
        services.AddSingleton(sp => new HostCredentials
        {
            // What the person at this computer decided earlier (the camera stays off until they switch it on).
            AllowedPermissions = sp.GetService<ISecretStore>() is { } store ? HostPolicy.Load(store) : HostPolicy.Default,
        });
        services.AddSingleton<AuthAttemptLimiter>();
        services.AddSingleton<ControllerSessionService>();
        services.AddSingleton<HostSessionService>();
        services.AddHostedService<TransportWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<HostSessionService>());
        return services;
    }

    /// <summary>
    /// Serves the desktop channels (screen, input, clipboard, audio) through the session agent instead of in this process. Used by
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
        services.AddSingleton<IFeatureModule>(sp => new AgentForwardingModule(
            ChannelType.Clipboard, Permissions.Clipboard, AgentChannelKind.Clipboard, replayFirstFrame: false,
            sp.GetRequiredService<IAgentGateway>(), sp.GetRequiredService<ILogger<AgentForwardingModule>>(),
            maxInboundFrame: 16 * 1024 * 1024));
        services.AddSingleton<IFeatureModule>(sp => new AgentForwardingModule(
            ChannelType.Audio, Permissions.Audio, AgentChannelKind.Audio, replayFirstFrame: true,
            sp.GetRequiredService<IAgentGateway>(), sp.GetRequiredService<ILogger<AgentForwardingModule>>()));

        // Files are handled as the signed-in user, which only the agent can be. Chunks are up to 1 MiB plus framing.
        services.AddSingleton<IFeatureModule>(sp => new AgentForwardingModule(
            ChannelType.FileTransfer, Permissions.FileTransfer, AgentChannelKind.File, replayFirstFrame: false,
            sp.GetRequiredService<IAgentGateway>(), sp.GetRequiredService<ILogger<AgentForwardingModule>>(),
            maxInboundFrame: 2 * 1024 * 1024, lossless: true));

        // Shells are the signed-in user's too. Keystrokes must not be dropped or replayed, so the channel is lossless and
        // simply ends if the agent is replaced (for example by the lock screen).
        services.AddSingleton<IFeatureModule>(sp => new AgentForwardingModule(
            ChannelType.Terminal, Permissions.Terminal, AgentChannelKind.Terminal, replayFirstFrame: false,
            sp.GetRequiredService<IAgentGateway>(), sp.GetRequiredService<ILogger<AgentForwardingModule>>(),
            lossless: true));

        // The camera belongs to the signed-in user as well. Lossless: a stream silently moved to another agent would not
        // decode, and the viewer simply opens the camera again.
        services.AddSingleton<IFeatureModule>(sp => new AgentForwardingModule(
            ChannelType.Camera, Permissions.Camera, AgentChannelKind.Camera, replayFirstFrame: false,
            sp.GetRequiredService<IAgentGateway>(), sp.GetRequiredService<ILogger<AgentForwardingModule>>(),
            lossless: true));
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
            isService,
            sp.GetService<ISecretStore>(),
            sp.GetService<TransportOptions>()?.RendezvousUri?.ToString()));
        return services;
    }
}
