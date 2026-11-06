namespace RemoteDesktop.Server.Relay;

public sealed class RelayOptions
{
    public const string SectionName = "Relay";

    /// <summary>UDP port peers send relay traffic to.</summary>
    public int Port { get; set; } = 5082;

    /// <summary>Base64 HMAC key shared with the rendezvous server, which signs the tickets.</summary>
    public string SharedKey { get; set; } = string.Empty;

    /// <summary>A pairing without traffic for this long is dropped.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How the console's node list names this relay.</summary>
    public string Name { get; set; } = "relay";

    /// <summary>
    /// Where the HTTP face listens: <c>/metrics</c>, <c>/health</c> and <c>/internal/v1/info</c>. Loopback by default —
    /// these numbers describe the deployment and are the console's business, not a visitor's.
    /// </summary>
    public string HttpEndpoint { get; set; } = "http://127.0.0.1:5083";

    /// <summary>
    /// Bearer token the console must present. Empty means only callers on the loopback interface are accepted, so a console
    /// on another machine needs it set. The same value is configured on the console and on the signaling server.
    /// </summary>
    public string NodeToken { get; set; } = string.Empty;

    /// <summary>
    /// Checks the settings the relay cannot work with. Called at start-up: a zero idle timeout used to clear every
    /// pairing seconds after it was made, with the process still reporting healthy.
    /// </summary>
    public void Validate()
    {
        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("Relay:Port must be between 1 and 65535.");
        }

        if (IdleTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Relay:IdleTimeout must be positive.");
        }

        if (string.IsNullOrWhiteSpace(HttpEndpoint) || !Uri.TryCreate(HttpEndpoint, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("Relay:HttpEndpoint must be an absolute URL.");
        }
    }
}
