namespace RemoteDesktop.Server.Rendezvous;

public sealed class RendezvousOptions
{
    public const string SectionName = "Rendezvous";

    /// <summary>UDP port of the address reflector; 0 picks a free port (tests).</summary>
    public int ReflectorPort { get; set; } = 5081;

    /// <summary>How long a connect request may wait for the host to answer.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Relay endpoint handed to clients, as "host:port". Empty disables the relay fallback.</summary>
    public string RelayEndpoint { get; set; } = string.Empty;

    /// <summary>Base64 HMAC key shared with the relay.</summary>
    public string RelaySharedKey { get; set; } = string.Empty;

    public TimeSpan RelayTicketLifetime { get; set; } = TimeSpan.FromMinutes(2);
}
