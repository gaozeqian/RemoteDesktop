using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Transport;

public enum ConnectionPath
{
    /// <summary>Direct UDP path (LAN or successful hole punch).</summary>
    Direct,

    /// <summary>Forwarded by the relay server; still end-to-end encrypted.</summary>
    Relayed,
}

/// <summary>
/// An end-to-end encrypted connection to a peer device, carrying multiplexed channels. Both certificates were pinned
/// to the fingerprints exchanged through the rendezvous server, so <see cref="RemoteFingerprint"/> is authenticated.
/// </summary>
public interface IPeerConnection : IAsyncDisposable
{
    DeviceId RemoteDevice { get; }

    ConnectionPath Path { get; }

    /// <summary>
    /// The server-minted session id both ends report under. Present on a direct path too, not only on
    /// <see cref="ConnectionPath.Relayed"/>: it is what ties a heartbeat or an end report to the signaling events for
    /// the same session. <c>null</c> when the server did not issue one (an older server, or no relay configured).
    /// </summary>
    string? RelaySessionId { get; }

    string LocalFingerprint { get; }

    string RemoteFingerprint { get; }

    ValueTask<IChannel> OpenChannelAsync(ChannelType type, bool bidirectional, CancellationToken cancellationToken);

    /// <summary>Waits for the peer to open a channel. Returns <c>null</c> when the connection closed.</summary>
    ValueTask<IChannel?> AcceptChannelAsync(CancellationToken cancellationToken);
}

public interface IChannel : IAsyncDisposable
{
    ChannelType Type { get; }

    Stream Stream { get; }
}
