using RemoteDesktop.Server.Storage;

namespace RemoteDesktop.Server.Rendezvous;

public sealed class RendezvousOptions
{
    public const string SectionName = "Rendezvous";

    /// <summary>UDP port of the address reflector; 0 picks a free port (tests).</summary>
    public int ReflectorPort { get; set; } = 5081;

    /// <summary>How the console's node list names this server.</summary>
    public string Name { get; set; } = "rendezvous";

    /// <summary>
    /// Bearer token the console must present to <c>/metrics</c> and <c>/internal/*</c>. Empty means no check, which is
    /// only acceptable on a loopback listener. The same value is configured on the relay and the console.
    /// </summary>
    public string NodeToken { get; set; } = string.Empty;

    /// <summary>How long a connect request may wait for the host to answer.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Relay endpoint handed to clients, as "host:port". Empty disables the relay fallback.</summary>
    public string RelayEndpoint { get; set; } = string.Empty;

    /// <summary>Base64 HMAC key shared with the relay.</summary>
    public string RelaySharedKey { get; set; } = string.Empty;

    public TimeSpan RelayTicketLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Where device ids are kept: none (memory), PostgreSQL or SQLite. The provider is explicit; when this section
    /// says nothing, <see cref="PostgresConnectionString"/> and <c>ConnectionStrings:Postgres</c> are still read, so
    /// an existing deployment keeps working without touching its settings.
    /// </summary>
    public DatabaseSettings Database { get; set; } = new();

    /// <summary>
    /// PostgreSQL connection string, as earlier versions spelled it. Superseded by
    /// <c>Rendezvous:Database:ConnectionString</c>; still honoured, because it is what every installed deployment has
    /// in its settings file and what every installer writes unless told otherwise.
    /// </summary>
    public string? PostgresConnectionString { get; set; }

    /// <summary>
    /// Answer presence queries, which is how a client shows an online/offline dot beside each remembered device.
    /// Turning it off is deliberately the same behaviour an older server has — clients show 未知 — so the capability
    /// can be removed from a deployment without a protocol change. See docs/design/README.md.
    /// </summary>
    public bool PresenceEnabled { get; set; } = true;

    /// <summary>
    /// Most devices served at once. A connection past this is refused before the WebSocket upgrade.
    /// </summary>
    public int MaxConnections { get; set; } = 4096;

    /// <summary>
    /// Most connections one source address may hold at once. Sized for a site behind one NAT — an office where
    /// everybody's machine comes online at nine — rather than for a single household.
    /// </summary>
    /// <remarks>
    /// This is also the ceiling on how many identities one address can have online at once, and an identity is what
    /// presence queries are counted in, so it is part of what bounds that exposure.
    /// </remarks>
    public int MaxConnectionsPerAddress { get; set; } = 256;

    /// <summary>
    /// New identities one source address may create: <see cref="RegistrationBurst"/> of them, then one every
    /// <see cref="RegistrationRefill"/>. A device coming back is not a new identity and does not spend from here.
    /// </summary>
    /// <remarks>
    /// Registration is what an identity costs, and identities are what every other budget on this server is counted
    /// in, so this is the limit that keeps the others meaningful. It is a speed bump and not a wall: without accounts
    /// there is nothing that makes an address scarce, and a determined attacker with many addresses still gets many
    /// identities. The burst is what a shared office connection needs on a Monday morning; the refill is what stops
    /// one address from minting them by the thousand.
    /// </remarks>
    public int RegistrationBurst { get; set; } = 256;

    public TimeSpan RegistrationRefill { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Connect requests one device may make: a burst, then one every <see cref="ConnectRequestRefill"/>.</summary>
    public int ConnectRequestBurst { get; set; } = 10;

    public TimeSpan ConnectRequestRefill { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Most connect requests one device may have waiting for an answer at the same time.</summary>
    public int MaxPendingRequestsPerDevice { get; set; } = 8;

    /// <summary>Ceiling on the server's whole pending-request table, whatever the requests came from.</summary>
    public int MaxPendingRequests { get; set; } = 4096;

    /// <summary>
    /// The reverse proxies in front of this server, as IP addresses or networks such as <c>10.0.0.0/8</c>. Only these
    /// may set the client's address through <c>X-Forwarded-For</c> — the header is trivially forged, so believing it
    /// from anybody would turn every per-address limit into a suggestion. Loopback is not special: a proxy on this
    /// machine has to be listed too, because "the connection came from 127.0.0.1" says nothing about who wrote the
    /// header.
    /// </summary>
    public string[] TrustedProxies { get; set; } = [];

    /// <summary>
    /// Serve the Prometheus metrics at <c>/metrics</c>. Off by default, and while it is off the path is not mapped at
    /// all, so it answers 404 like any other address this server does not have.
    /// </summary>
    /// <remarks>
    /// The numbers say how many devices this public server is serving and how hard it is refusing the rest, which is
    /// the operator's business rather than a visitor's. Turning it on is therefore deliberate, and normally combined
    /// with a listener only one's own monitoring can reach — a loopback address behind a proxy, a firewall rule, or
    /// the monitoring's own network. See docs/ops/README.md.
    /// </remarks>
    public bool MetricsEnabled { get; set; }

    /// <summary>
    /// Checks the settings the server cannot work without. Called when the options are first used (ValidateOnStart), so
    /// a misconfigured deployment stops at start-up with a sentence instead of failing later on the first connection.
    /// </summary>
    public void Validate()
    {
        ValidateLimits();
        ValidateTrustedProxies();

        if (string.IsNullOrEmpty(RelayEndpoint))
        {
            return;
        }

        if (!System.Net.IPEndPoint.TryParse(RelayEndpoint, out _))
        {
            // An address and not a name: this value is handed to the clients, which send UDP straight to it.
            throw new InvalidOperationException($"Rendezvous:RelayEndpoint must be an address and port, like 203.0.113.10:5082 (got '{RelayEndpoint}'). Clients are sent straight to it, so a name will not do.");
        }

        // The key signs the tickets that let a peer use the relay; an empty or short one would let anybody relay through
        // this server. The relay checks the same thing on its side, so a mismatch is caught at start-up as well.
        var key = Array.Empty<byte>();
        try
        {
            key = string.IsNullOrEmpty(RelaySharedKey) ? [] : Convert.FromBase64String(RelaySharedKey);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Rendezvous:RelaySharedKey must be base64 (generate one with: openssl rand -base64 48).");
        }

        if (key.Length < 32)
        {
            throw new InvalidOperationException(
                $"Rendezvous:RelaySharedKey must be at least 32 bytes when Rendezvous:RelayEndpoint is set (it is {(key.Length == 0 ? "empty" : $"only {key.Length} bytes")}). Generate one with: openssl rand -base64 48");
        }
    }

    /// <summary>
    /// A limit of zero would refuse everything and a negative one would behave as no limit at all, so the two are not
    /// the same mistake and neither is worth discovering from the outside.
    /// </summary>
    private void ValidateLimits()
    {
        Positive(MaxConnections, nameof(MaxConnections));
        Positive(MaxConnectionsPerAddress, nameof(MaxConnectionsPerAddress));
        Positive(RegistrationBurst, nameof(RegistrationBurst));
        Positive(ConnectRequestBurst, nameof(ConnectRequestBurst));
        Positive(MaxPendingRequests, nameof(MaxPendingRequests));
        Positive(MaxPendingRequestsPerDevice, nameof(MaxPendingRequestsPerDevice));
        Positive(RegistrationRefill, nameof(RegistrationRefill));
        Positive(ConnectRequestRefill, nameof(ConnectRequestRefill));
    }

    private void ValidateTrustedProxies()
    {
        foreach (var entry in TrustedProxies)
        {
            // Parsed here as well as where the middleware is wired up, so a typo stops the deployment at start-up
            // rather than quietly leaving the real client address unknown.
            if (!System.Net.IPAddress.TryParse(entry, out _) && !System.Net.IPNetwork.TryParse(entry, out _))
            {
                throw new InvalidOperationException($"Rendezvous:TrustedProxies must be IP addresses or networks such as 10.0.0.0/8 (got '{entry}').");
            }
        }
    }

    private static void Positive(int value, string name)
    {
        if (value <= 0)
        {
            throw new InvalidOperationException($"Rendezvous:{name} must be greater than zero (got {value}).");
        }
    }

    private static void Positive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"Rendezvous:{name} must be greater than zero (got {value}).");
        }
    }
}
