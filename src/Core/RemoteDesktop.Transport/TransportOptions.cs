using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using RemoteDesktop.Domain;

namespace RemoteDesktop.Transport;

public sealed class TransportOptions
{
    /// <summary>Signaling WebSocket, e.g. <c>wss://rd.example.com/ws</c>.</summary>
    public Uri RendezvousUri { get; set; } = new("ws://127.0.0.1:5080/ws");

    /// <summary>
    /// Allows plain <c>ws://</c> to a non-loopback server. It is off by default because signaling carries device
    /// codes and relay tickets; deployments that deliberately keep signaling on an isolated plaintext network can
    /// turn it on after deciding that risk is acceptable.
    /// </summary>
    public bool AllowInsecureSignaling { get; set; }

    /// <summary>
    /// A server certificate to pin for a <c>wss://</c> signaling connection, as a PEM or DER file. Empty means the
    /// platform trust store decides, which is right for a certificate from a public authority.
    /// </summary>
    /// <remarks>
    /// Why a pin instead of adding the self-signed certificate to the System keychain: the macOS installer scripts run
    /// as root without a user session, and <c>security add-trusted-cert</c> then fails with "The authorization was
    /// denied since no user interaction was possible". Pinning is also narrower than a trust anchor — only this one
    /// certificate is accepted — and it is what the installer writes when the person selects the server's public half.
    /// </remarks>
    public string? RendezvousCertificatePath { get; set; }

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
        if (RendezvousUri is null || !RendezvousAddress.TryParse(RendezvousUri.OriginalString, out var parsed))
        {
            throw new InvalidOperationException(
                $"Transport:RendezvousUri must be a ws:// or wss:// address with a host and the server's /ws path (got '{RendezvousUri}').");
        }

        // The parser fills in /ws for an address typed without a path and keeps any other path (a reverse proxy may use
        // a prefix). Storing the parsed form means the connection uses the path the server actually maps, whichever
        // spelling the operator used.
        RendezvousUri = parsed.Value;

        if (RendezvousUri.Scheme == "ws" && !RendezvousUri.IsLoopback && !AllowInsecureSignaling)
        {
            throw new InvalidOperationException(
                $"Transport:RendezvousUri uses plain ws:// to non-loopback host '{RendezvousUri.Host}'. Signaling carries device codes and relay tickets; use wss:// (or set Transport:AllowInsecureSignaling=true only for an isolated plaintext network).");
        }

        // A pin left behind after the address is changed to ws:// is harmless: there is no TLS to validate. It is only
        // read when the address is wss://, and a missing/broken file there is a startup error, not a silent fallback.
        if (!string.IsNullOrWhiteSpace(RendezvousCertificatePath) && RendezvousUri.Scheme == "wss")
        {
            try
            {
                using var certificate = X509CertificateLoader.LoadCertificateFromFile(RendezvousCertificatePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                throw new InvalidOperationException($"Transport:RendezvousCertificatePath '{RendezvousCertificatePath}' could not be read: {ex.Message}", ex);
            }
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
