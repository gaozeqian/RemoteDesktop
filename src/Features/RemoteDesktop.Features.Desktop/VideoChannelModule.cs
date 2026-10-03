using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Features.Desktop;

/// <summary>Streams encoded display frames from host to controller; used by both control and watch mode.</summary>
public sealed class VideoChannelModule : IFeatureModule
{
    public ChannelType ChannelType => ChannelType.Video;

    public Permissions RequiredPermission => Permissions.ViewScreen;

    public Task HandleAsync(IChannel channel, SessionContext session, CancellationToken cancellationToken) =>
        throw new NotImplementedException("Planned for milestone M2.");
}
