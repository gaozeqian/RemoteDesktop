namespace RemoteDesktop.Server.Shared;

/// <summary>
/// The metric names the console reads. Shared so a rename is one edit rather than three that drift: the server that
/// emits a series and the console that draws it must agree on the spelling, and nothing in the compiler checks a string
/// that travels through HTTP.
/// </summary>
public static class OpsMetricNames
{
    /// <summary>Gauge: devices holding a signaling connection right now.</summary>
    public const string DevicesOnline = "remotedesktop_devices_online";

    /// <summary>Gauge: fully paired relay sessions right now.</summary>
    public const string RelayPairingsActive = "remotedesktop_relay_pairings_active";

    /// <summary>Counter: bytes the relay forwarded from controllers to hosts.</summary>
    public const string RelayBytesToHost = "remotedesktop_relay_bytes_to_host_total";

    /// <summary>Counter: bytes the relay forwarded from hosts to controllers.</summary>
    public const string RelayBytesToController = "remotedesktop_relay_bytes_to_controller_total";

    /// <summary>Counter: datagrams the relay forwarded in either direction.</summary>
    public const string RelayPackets = "remotedesktop_relay_packets_total";

    /// <summary>Counter: pairings that completed since the process started.</summary>
    public const string RelayPairingsTotal = "remotedesktop_relay_pairings_total";

    /// <summary>Counter: relay tickets the signaling server issued.</summary>
    public const string RelayTicketsIssued = "remotedesktop_relay_tickets_issued_total";
}
