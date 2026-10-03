using System.Net;
using RemoteDesktop.Domain;
using RemoteDesktop.Signaling.Messages;

namespace RemoteDesktop.Transport.Rendezvous;

public enum RendezvousState
{
    Offline,
    Connecting,
    Online,
}

/// <summary>Signaling connection to the rendezvous server: presence, device id and connect brokering.</summary>
public interface IRendezvousClient
{
    RendezvousState State { get; }

    /// <summary>Assigned by the server; <c>null</c> until the first registration succeeds.</summary>
    DeviceId? DeviceId { get; }

    /// <summary>UDP endpoint of the server's address reflector, known once online.</summary>
    IPEndPoint? ReflectorEndPoint { get; }

    event EventHandler? StateChanged;

    /// <summary>Keeps the connection alive, reconnecting with back-off, until cancelled.</summary>
    Task RunAsync(CancellationToken cancellationToken);

    /// <summary>Connection requests addressed to this device.</summary>
    IAsyncEnumerable<IncomingConnection> ReadIncomingAsync(CancellationToken cancellationToken);

    /// <exception cref="PeerConnectException">Offline, target offline, rejected or timed out.</exception>
    Task<ConnectReady> RequestConnectionAsync(DeviceId target, IEnumerable<PeerEndpoint> candidates, CancellationToken cancellationToken);

    Task AcceptAsync(string requestId, IEnumerable<PeerEndpoint> candidates, CancellationToken cancellationToken);

    Task RejectAsync(string requestId, CancellationToken cancellationToken);
}
