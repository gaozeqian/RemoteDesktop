using RemoteDesktop.Domain;

namespace RemoteDesktop.Transport;

/// <summary>
/// Establishes a connection to a remote device through the rendezvous server: exchange candidates and certificate
/// fingerprints, try hole punching, fall back to the relay.
/// </summary>
public interface IPeerConnector
{
    /// <exception cref="PeerConnectException">The device is offline, refused, or no path worked.</exception>
    Task<IPeerConnection> ConnectAsync(DeviceId remote, CancellationToken cancellationToken);
}

/// <summary>Host side: accepts connections arriving through any path.</summary>
public interface IPeerListener
{
    /// <summary>Prepares for incoming connection requests until <paramref name="cancellationToken"/> fires.</summary>
    Task RunAsync(CancellationToken cancellationToken);

    ValueTask<IPeerConnection> AcceptAsync(CancellationToken cancellationToken);
}

public enum PeerConnectFailure
{
    SignalingUnavailable,
    TargetOffline,
    Rejected,
    Timeout,
    NoPath,
}

public sealed class PeerConnectException : Exception
{
    public PeerConnectException()
    {
    }

    public PeerConnectException(string message)
        : base(message)
    {
    }

    public PeerConnectException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PeerConnectException(PeerConnectFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public PeerConnectFailure Failure { get; }
}
