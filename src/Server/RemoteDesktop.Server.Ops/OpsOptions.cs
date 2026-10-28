using System.Net;
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
    /// Allows the node token to travel over plain HTTP to non-loopback addresses. Off by default because a public
    /// network would expose the bearer credential. Enable only for an isolated deployment network such as Compose.
    /// </summary>
    public bool AllowInsecureNodeHttp { get; set; }

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

    /// <summary>
    /// Proxies whose <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> headers are believed. Empty by default: a
    /// header anybody can write must not decide the audit address, the sign-in budget, or whether a cookie is Secure.
    /// A same-machine nginx/Caddy is named here like any other proxy.
    /// </summary>
    public List<string> TrustedProxies { get; set; } = [];

    /// <summary>How long a stuck management action may stay approved before the recovery task closes it.</summary>
    public int ActionStaleSeconds { get; set; } = 60;

    /// <summary>How much history the console keeps in its own database.</summary>
    public RetentionSettings Retention { get; set; } = new();

    /// <summary>Refuses settings that cannot work, before the host starts serving.</summary>
    /// <exception cref="InvalidOperationException">A value cannot be used; the message names the key.</exception>
    public void Validate()
    {
        // The binder can turn an explicit null in appsettings into a null property even though the defaults are
        // non-null; normalise before anything dereferences them.
        Database ??= new OpsDatabaseSettings();
        Notifications ??= new NotificationSettings();
        Ingest ??= new IngestSettings();
        Retention ??= new RetentionSettings();
        TrustedProxies ??= [];

        Database.Validate();

        if (ScrapeIntervalSeconds < 1)
        {
            throw new InvalidOperationException("Ops:ScrapeIntervalSeconds must be at least 1.");
        }

        if (ScrapeTimeoutSeconds < 1)
        {
            throw new InvalidOperationException("Ops:ScrapeTimeoutSeconds must be at least 1.");
        }

        if (AlertIntervalSeconds < 1)
        {
            throw new InvalidOperationException("Ops:AlertIntervalSeconds must be at least 1.");
        }

        if (ActionStaleSeconds < 1)
        {
            throw new InvalidOperationException("Ops:ActionStaleSeconds must be at least 1.");
        }

        if (SessionLifetime <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Ops:SessionLifetime must be positive.");
        }

        if (!string.IsNullOrWhiteSpace(Notifications.WebhookUrl)
            && (!Uri.TryCreate(Notifications.WebhookUrl.Trim(), UriKind.Absolute, out var webhook)
                || (webhook.Scheme != Uri.UriSchemeHttp && webhook.Scheme != Uri.UriSchemeHttps)))
        {
            throw new InvalidOperationException(
                "Ops:Notifications:WebhookUrl must be an absolute http(s) URL, or empty to disable notifications.");
        }

        foreach (var entry in TrustedProxies)
        {
            if (!IPAddress.TryParse(entry, out _) && !System.Net.IPNetwork.TryParse(entry, out _))
            {
                throw new InvalidOperationException(
                    $"Ops:TrustedProxies contains '{entry}', which is neither an IP address nor a CIDR network.");
            }
        }

        Retention.Validate();
    }
}

/// <summary>Retention windows, in days. Zero disables the delete for that table.</summary>
public sealed class RetentionSettings
{
    /// <summary>Raw metric samples. The overview reads minutes, not months.</summary>
    public int SampleDays { get; set; } = 30;

    /// <summary>Ingested events. A session's timeline is useful while the session list still shows it.</summary>
    public int EventDays { get; set; } = 30;

    /// <summary>Projected sessions.</summary>
    public int SessionDays { get; set; } = 90;

    /// <summary>Resolved alerts. Open alerts are never removed.</summary>
    public int AlertDays { get; set; } = 180;

    /// <summary>How often the maintenance task runs.</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>Refuses negative windows and an interval that would spin.</summary>
    public void Validate()
    {
        if (SampleDays < 0 || EventDays < 0 || SessionDays < 0 || AlertDays < 0)
        {
            throw new InvalidOperationException("Ops:Retention:* values cannot be negative.");
        }

        if (IntervalMinutes < 1)
        {
            throw new InvalidOperationException("Ops:Retention:IntervalMinutes must be at least 1.");
        }
    }
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
