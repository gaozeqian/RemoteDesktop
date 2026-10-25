using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops;

/// <summary>What the console is told at start-up. Bound from the <c>Ops</c> section.</summary>
public sealed class OpsOptions
{
    public const string SectionName = "Ops";

    /// <summary>
    /// Where Kestrel listens. Loopback by default: the console has its own sign-in and no rate limits worth trusting
    /// on a public socket, so exposing it is a reverse-proxy decision, not a default.
    /// </summary>
    public string Listen { get; set; } = "http://127.0.0.1:5090";

    /// <summary>Where the console keeps its own data. Required; there is no in-memory fallback.</summary>
    public OpsDatabaseSettings Database { get; set; } = new();

    /// <summary>
    /// Require a TOTP code after the password. On by default: an ops console is the one account whose theft is worth
    /// planning for, and turning this off should be a sentence in a deployment document.
    /// </summary>
    public bool RequireTotp { get; set; } = true;

    /// <summary>How long a signed-in browser stays signed in.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>How often every enabled node is scraped.</summary>
    public int ScrapeIntervalSeconds { get; set; } = 10;

    /// <summary>How long one scrape may take before it counts as failed.</summary>
    public int ScrapeTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Bearer token sent to every node's <c>/metrics</c> and <c>/internal/*</c>. Empty means none is sent, which only
    /// works while the nodes are bound to loopback. The same value is configured on the signaling and relay servers.
    /// </summary>
    public string NodeToken { get; set; } = string.Empty;

    /// <summary>
    /// A certificate to pin for calls to nodes, as a PEM or DER file. Empty means the system trust store decides.
    /// </summary>
    /// <remarks>
    /// The macOS server package writes the signaling server's certificate here when signaling uses TLS. A .pkg's
    /// postinstall cannot add a certificate to the System keychain — macOS refuses the trust change without a user
    /// session — so the console trusts exactly this certificate instead. Only the local nodes use it; webhooks keep the
    /// platform's normal validation.
    /// </remarks>
    public string NodeCertificatePath { get; set; } = string.Empty;

    /// <summary>How servers hand the console their events.</summary>
    public IngestSettings Ingest { get; set; } = new();

    /// <summary>How often enabled rules are evaluated.</summary>
    public int AlertIntervalSeconds { get; set; } = 15;

    /// <summary>Where alerts are announced.</summary>
    public NotificationSettings Notifications { get; set; } = new();
}

/// <summary>
/// Outgoing notifications. One webhook for the whole console rather than a channel table: the common receiver is a
/// group-chat robot, the URL is the whole configuration, and a table with no UI would be a table nobody fills in.
/// </summary>
public sealed class NotificationSettings
{
    /// <summary>Receives a JSON POST when an alert opens or resolves. Empty disables notifications.</summary>
    public string WebhookUrl { get; set; } = string.Empty;
}

/// <summary>
/// The shared keys servers sign event batches with. More than one so a key can be rotated without a window in which
/// nodes are refused: add the new key, roll the nodes, remove the old one.
/// </summary>
public sealed class IngestSettings
{
    /// <summary>Key id to Base64 HMAC key. Empty disables the endpoint — a console with no keys is not collecting.</summary>
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);
}
