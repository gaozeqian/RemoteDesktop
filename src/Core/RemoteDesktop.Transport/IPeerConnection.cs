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

/// <summary>An authenticated, end-to-end encrypted connection to a peer device, carrying multiplexed channels.</summary>
public interface IPeerConnection : IAsyncDisposable
{
    DeviceId RemoteDevice { get; }

    ConnectionPath Path { get; }

    /// <summary>Smoothed round-trip time, used for bitrate adaptation.</summary>
    TimeSpan RoundTripTime { get; }

    ValueTask<IChannel> OpenChannelAsync(ChannelType type, bool bidirectional, CancellationToken cancellationToken);

    /// <summary>Waits for the peer to open a channel. Returns <c>null</c> when the connection closed.</summary>
    ValueTask<IChannel?> AcceptChannelAsync(CancellationToken cancellationToken);
}

public interface IChannel : IAsyncDisposable
{
    ChannelType Type { get; }

    Stream Stream { get; }
}
