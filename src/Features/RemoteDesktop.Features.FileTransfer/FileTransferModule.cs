using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Features.FileTransfer;

/// <summary>Chunked, resumable file transfer with hash verification; one channel per transfer task.</summary>
public sealed class FileTransferModule : IFeatureModule
{
    public ChannelType ChannelType => ChannelType.FileTransfer;

    public Permissions RequiredPermission => Permissions.FileTransfer;

    public Task HandleAsync(IChannel channel, SessionContext session, CancellationToken cancellationToken) =>
        throw new NotImplementedException("Planned for milestone M5.");
}
