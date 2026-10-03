using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Features.Terminal;

/// <summary>Bridges a host pseudo-terminal to the controller; one channel per terminal.</summary>
public sealed class TerminalModule : IFeatureModule
{
    public ChannelType ChannelType => ChannelType.Terminal;

    public Permissions RequiredPermission => Permissions.Terminal;

    public Task HandleAsync(IChannel channel, SessionContext session, CancellationToken cancellationToken) =>
        throw new NotImplementedException("Planned for milestone M6.");
}
