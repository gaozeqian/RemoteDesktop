using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Application.Features;

/// <summary>
/// A remote feature (desktop, file transfer, terminal, camera...). Adding a feature means adding a module;
/// the session core does not change.
/// </summary>
public interface IFeatureModule
{
    ChannelType ChannelType { get; }

    /// <summary>Permission the host requires before it accepts a channel of <see cref="ChannelType"/>.</summary>
    Permissions RequiredPermission { get; }

    /// <summary>Serves one channel until it closes or <paramref name="cancellationToken"/> fires.</summary>
    Task HandleAsync(IChannel channel, SessionContext session, CancellationToken cancellationToken);
}
