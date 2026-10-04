using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.SessionAgent;

/// <summary>A feature channel whose bytes travel over a pipe to the host service instead of a peer connection.</summary>
internal sealed class PipeChannel(ChannelType type, Stream stream) : IChannel
{
    public ChannelType Type { get; } = type;

    public Stream Stream { get; } = stream;

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

/// <summary>
/// Stands in for the peer connection, which lives in the host service. The feature modules take a session context
/// but the ones that run here (screen, input) never touch the connection: the service already authorized the channel.
/// </summary>
internal sealed class DetachedPeerConnection : IPeerConnection
{
    public Domain.DeviceId RemoteDevice => default;

    public ConnectionPath Path => ConnectionPath.Direct;

    public string LocalFingerprint => string.Empty;

    public string RemoteFingerprint => string.Empty;

    public ValueTask<IChannel> OpenChannelAsync(ChannelType type, bool bidirectional, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Channels are opened by the host service.");

    public ValueTask<IChannel?> AcceptChannelAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("Channels are opened by the host service.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
