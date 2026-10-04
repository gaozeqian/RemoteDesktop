namespace RemoteDesktop.Transport;

public sealed class TransportOptions
{
    /// <summary>Signaling WebSocket, e.g. <c>wss://rd.example.com/ws</c>.</summary>
    public Uri RendezvousUri { get; set; } = new("ws://127.0.0.1:5080/ws");

    /// <summary>Skip direct candidates and always use the relay (diagnostics, tests).</summary>
    public bool ForceRelay { get; set; }

    /// <summary>QUIC handshake budget per direct candidate.</summary>
    public TimeSpan DirectAttemptTimeout { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan RelayAttemptTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Budget for the reflector query and the relay bind.</summary>
    public TimeSpan UdpRequestTimeout { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>How long a signaling answer may take before the client gives up.</summary>
    public TimeSpan SignalingTimeout { get; set; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// False on a client whose machine is hosted by the background service: the UI then only makes outgoing
    /// connections and leaves "being controlled" to the service.
    /// </summary>
    public bool AcceptIncoming { get; set; } = true;

    /// <summary>How long the host waits for the controller's QUIC connection after accepting.</summary>
    public TimeSpan HostAcceptTimeout { get; set; } = TimeSpan.FromSeconds(20);
}
