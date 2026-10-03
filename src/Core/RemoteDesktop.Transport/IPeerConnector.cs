using RemoteDesktop.Domain;

namespace RemoteDesktop.Transport;

/// <summary>
/// Establishes a connection to a remote device through the rendezvous server: exchange candidates and certificate
/// fingerprints, try hole punching, fall back to the relay.
/// </summary>
public interface IPeerConnector
{
    Task<IPeerConnection> ConnectAsync(DeviceId remote, CancellationToken cancellationToken);
}

/// <summary>Host side: accepts connections arriving through any path.</summary>
public interface IPeerListener : IAsyncDisposable
{
    ValueTask<IPeerConnection> AcceptAsync(CancellationToken cancellationToken);
}
