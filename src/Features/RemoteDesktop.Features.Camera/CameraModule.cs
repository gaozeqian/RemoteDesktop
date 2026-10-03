using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Features.Camera;

/// <summary>Streams a host camera to the controller through the shared video encoding pipeline.</summary>
public sealed class CameraModule : IFeatureModule
{
    public ChannelType ChannelType => ChannelType.Camera;

    public Permissions RequiredPermission => Permissions.Camera;

    public Task HandleAsync(IChannel channel, SessionContext session, CancellationToken cancellationToken) =>
        throw new NotImplementedException("Planned for milestone M7.");
}
