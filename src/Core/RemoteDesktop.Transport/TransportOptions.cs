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

    /// <summary>
    /// Checks the settings a host cannot run without. Called while the host is starting, so a mistyped address or a
    /// timeout of zero stops the program with a sentence instead of throwing from somewhere deep inside a timer.
    /// </summary>
    /// <exception cref="InvalidOperationException">A setting cannot be used; the message names it.</exception>
    public void Validate()
    {
        if (RendezvousUri is null || !RendezvousUri.IsAbsoluteUri || RendezvousUri.Host.Length == 0)
        {
            throw new InvalidOperationException($"Transport:RendezvousUri must be an absolute address (got '{RendezvousUri}').");
        }

        if (RendezvousUri.Scheme is not ("ws" or "wss"))
        {
            throw new InvalidOperationException($"Transport:RendezvousUri must be ws:// or wss:// (got '{RendezvousUri}').");
        }

        Positive(DirectAttemptTimeout, nameof(DirectAttemptTimeout));
        Positive(RelayAttemptTimeout, nameof(RelayAttemptTimeout));
        Positive(UdpRequestTimeout, nameof(UdpRequestTimeout));
        Positive(SignalingTimeout, nameof(SignalingTimeout));
        Positive(HostAcceptTimeout, nameof(HostAcceptTimeout));
    }

    private static void Positive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"Transport:{name} must be greater than zero (got {value}).");
        }
    }
}
