using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Features.Desktop;

/// <summary>Injects controller input on the host. Never reachable in watch mode, because the grant excludes ControlInput.</summary>
public sealed class InputChannelModule : IFeatureModule
{
    public ChannelType ChannelType => ChannelType.Input;

    public Permissions RequiredPermission => Permissions.ControlInput;

    public Task HandleAsync(IChannel channel, SessionContext session, CancellationToken cancellationToken) =>
        throw new NotImplementedException("Planned for milestone M2.");
}
