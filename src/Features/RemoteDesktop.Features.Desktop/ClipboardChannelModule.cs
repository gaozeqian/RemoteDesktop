using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Clipboard;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Features.Desktop;

/// <summary>
/// Host side of clipboard sharing. Not available in watch mode: the grant then lacks <see cref="Permissions.Clipboard"/>,
/// and the dispatcher never opens the channel.
/// </summary>
public sealed class ClipboardChannelModule(IClipboard clipboard, ILoggerFactory loggers) : IFeatureModule
{
    public ChannelType ChannelType => ChannelType.Clipboard;

    public Permissions RequiredPermission => Permissions.Clipboard;

    public Task HandleAsync(IChannel channel, SessionContext session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new ClipboardSynchronizer(clipboard, loggers.CreateLogger<ClipboardSynchronizer>()).RunAsync(channel.Stream, cancellationToken);
    }
}
