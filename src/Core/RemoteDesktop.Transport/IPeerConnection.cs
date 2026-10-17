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
    /// The relay session carrying this connection, when <see cref="Path"/> is <see cref="ConnectionPath.Relayed"/>.
    /// <c>null</c> on a direct path, and also when the server did not say (an older server).
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
