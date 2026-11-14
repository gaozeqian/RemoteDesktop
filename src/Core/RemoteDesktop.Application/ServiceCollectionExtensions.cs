using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Agent;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Clipboard;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Transport;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Application;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Session use cases. The host composition must also register a transport (e.g. <c>AddQuicTransport</c>).
    /// </summary>
    /// <param name="telemetry">
    /// Whether this device reports session quality, and how often. Off when omitted: the feature is opt-in per
    /// deployment, not per call site.
    /// </param>
    public static IServiceCollection AddRemoteDesktopApplication(this IServiceCollection services, TelemetryOptions? telemetry = null)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ConnectionAdmission>();
        services.AddSingleton(telemetry ?? new TelemetryOptions());
        services.AddSingleton<SessionTelemetry>();
        services.AddSingleton<SessionLifecycle>();
        services.AddSingleton<ChannelDispatcher>();
        services.AddSingleton(sp =>
        {
            var store = sp.GetService<ISecretStore>();
            return new HostCredentials(sp.GetRequiredService<TimeProvider>())
            {
                // The policy says what this host is willing to allow; the mask says what it is able to. The mask is
                // what keeps Ctrl+Alt+Del off sessions against a host that cannot raise it.
                AllowedPermissions = HostPolicy.Default & SupportedPermissions(sp),
                FixedPassword = store is not null ? FixedPasswordPolicy.Load(store) : null,
            };
        });
        services.AddSingleton<AuthAttemptLimiter>();
        services.AddSingleton<ControllerSessionService>();
        services.AddSingleton<HostSessionService>();
        services.AddHostedService<TransportWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<HostSessionService>());
        services.AddHostedService<AdminCommandService>();
        return services;
    }

    /// <summary>
    /// The permissions this composition can actually serve, as a mask to intersect the host's policy with.
    /// </summary>
    /// <remarks>
    /// A fact about the composition rather than about the operating system. On Windows the in-process injector cannot
    /// raise Ctrl+Alt+Del either — only a service may — so <see cref="Permissions.SecureAttention"/> is kept exactly
    /// when an agent gateway is present (the agent asks that service) or when the injector in this process says it can.
    /// macOS has no such sequence at all. Intersecting the policy with this leaves the permission absent on such a
    /// host, and the controller's UI reads the grant to decide whether to offer the button.
    /// <para>
    /// A mask, not a set of permissions to add: intersecting with <see cref="Permissions.None"/> would leave nothing
    /// at all, which is the opposite of what "this host cannot do Ctrl+Alt+Del" is supposed to mean.
    /// </para>
    /// </remarks>
    private static Permissions SupportedPermissions(IServiceProvider services) =>
        services.GetService<IAgentGateway>() is not null
            || services.GetService<IInputInjector>()?.SupportsSecureAttention == true
                ? Permissions.All
                : Permissions.All & ~Permissions.SecureAttention;

    /// <summary>
    /// The channels a background service forwards to the agent, in the order the dispatcher sees them.
    /// </summary>
    /// <remarks>
    /// One table rather than seven registrations, because the host-feature helper and this one have to serve the same
    /// channels: a feature added to only one of them used to be invisible until a controller asked for it. The limits
    /// are the protocol's own constants, not slack copied into the registration.
    /// </remarks>
    private static readonly ForwardedChannel[] ForwardedChannels =
    [
        new(ChannelType.Video, Permissions.ViewScreen, AgentChannelKind.Video, ReplayFirstFrame: true),
        new(ChannelType.Input, Permissions.ControlInput, AgentChannelKind.Input, ReplayFirstFrame: false, Lossless: true),
        new(ChannelType.Clipboard, Permissions.Clipboard, AgentChannelKind.Clipboard, ReplayFirstFrame: false,
            MaxInboundFrame: ClipboardFrame.MaxFrameBytes),
        new(ChannelType.Audio, Permissions.Audio, AgentChannelKind.Audio, ReplayFirstFrame: true),
        // Files are handled as the signed-in user, which only the agent can be. A chunk plus its frame header is the
        // largest thing the controller sends on this channel.
        new(ChannelType.FileTransfer, Permissions.FileTransfer, AgentChannelKind.File, ReplayFirstFrame: false,
            MaxInboundFrame: FileChunkFrame.ChunkSize + FrameCodec.HeaderSize, Lossless: true),
        // Shells are the signed-in user's too. Keystrokes must not be dropped or replayed, so the channel is lossless
        // and simply ends if the agent is replaced (for example by the lock screen).
        new(ChannelType.Terminal, Permissions.Terminal, AgentChannelKind.Terminal, ReplayFirstFrame: false, Lossless: true),
        // The camera belongs to the signed-in user as well. Lossless: a stream silently moved to another agent would
        // not decode, and the viewer simply opens the camera again.
        new(ChannelType.Camera, Permissions.Camera, AgentChannelKind.Camera, ReplayFirstFrame: false, Lossless: true),
    ];

    /// <summary>
    /// Serves the desktop channels (screen, input, clipboard, audio) through the session agent instead of in this process. Used by
    /// the background service, which cannot touch the interactive desktop itself. Requires an <see cref="IAgentGateway"/>.
    /// </summary>
    public static IServiceCollection AddAgentForwardedDesktop(this IServiceCollection services)
    {
        foreach (var channel in ForwardedChannels)
        {
            services.AddSingleton<IFeatureModule>(sp => new AgentForwardingModule(
                channel.Type,
                channel.Permission,
                channel.AgentKind,
                channel.ReplayFirstFrame,
                sp.GetRequiredService<IAgentGateway>(),
                sp.GetRequiredService<ILogger<AgentForwardingModule>>(),
                maxInboundFrame: channel.MaxInboundFrame,
                lossless: channel.Lossless));
        }

        return services;
    }

    private sealed record ForwardedChannel(
        ChannelType Type,
        Permissions Permission,
        AgentChannelKind AgentKind,
        bool ReplayFirstFrame,
        int MaxInboundFrame = AgentForwardingModule.DefaultMaxInboundFrame,
        bool Lossless = false);

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
            sp.GetService<TransportOptions>()?.RendezvousUri?.ToString(),
            sp.GetService<ICameraAccess>()));
        return services;
    }
}
