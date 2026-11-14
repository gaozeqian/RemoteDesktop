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
    /// Hosts (DNS names or IP addresses) the node token may be sent to, besides loopback. Empty — the default — allows
    /// any host the transport rules permit, which is what a deployment whose administrators are as trusted as its
    /// server needs. Set it to the servers this deployment runs and a node row pointed anywhere else is refused before
    /// the token leaves the process, so an administrator session alone cannot turn the console into a way to leak it.
    /// </summary>
    /// <remarks>
    /// Entries are plain host names: no scheme, no port, no path. Matching is case-insensitive and exact; loopback
    /// (127.0.0.1, ::1, localhost) is always allowed because the installers seed the local nodes there.
    /// </remarks>
    public List<string> NodeTokenHosts { get; set; } = [];

    /// <summary>
    /// A certificate to pin for calls to nodes, as a PEM or DER file. Empty means the system trust store decides.
    /// </summary>
    /// <remarks>
    /// The server packages on all three platforms write the signaling server's certificate here when signaling uses
    /// TLS. On macOS a .pkg's postinstall cannot add a certificate to the System keychain — macOS refuses the trust
    /// change without a user session — and the generated certificate is not in the system trust store elsewhere
    /// either, so the console trusts exactly this certificate instead. It applies to every call the console makes to
    /// a node (metrics scraping and device queries), not just the local ones; webhooks keep the platform's normal
    /// validation.
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

    /// <summary>
    /// Whether the 10-minute rollup task runs.
    /// </summary>
    /// <remarks>
    /// Off by default because nothing in the product reads <c>ops_metric_rollup</c> yet: writing 1-minute and 1-hour
    /// buckets every ten minutes is pure cost until a trend view consumes them. The code stays; a deployment that
    /// adds such a view turns this on together with <see cref="RetentionSettings.RollupDays"/> (for example 365) so
    /// the buckets are retained long enough to be read.
    /// </remarks>
    public bool RollupEnabled { get; set; }

    /// <summary>Refuses settings that cannot work, before the host starts serving.</summary>
    /// <exception cref="InvalidOperationException">A value cannot be used; the message names the key.</exception>
    public void Validate()
    {
        // The binder can turn an explicit null in appsettings into a null property even though the defaults are
        // non-null; normalise before anything dereferences them.
        Database ??= new OpsDatabaseSettings();
        Notifications ??= new NotificationSettings();
        Notifications.Smtp ??= new SmtpSettings();
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

        Notifications.Smtp.Validate();

        foreach (var entry in TrustedProxies)
        {
            if (!IPAddress.TryParse(entry, out _) && !System.Net.IPNetwork.TryParse(entry, out _))
            {
                throw new InvalidOperationException(
                    $"Ops:TrustedProxies contains '{entry}', which is neither an IP address nor a CIDR network.");
            }
        }

        NodeTokenHosts ??= [];
        for (var index = 0; index < NodeTokenHosts.Count; index++)
        {
            var host = NodeTokenHosts[index]?.Trim();
            if (string.IsNullOrEmpty(host)
                || host.Contains("://", StringComparison.Ordinal)
                || host.Contains('/', StringComparison.Ordinal)
                || host.Contains('\\', StringComparison.Ordinal)
                || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            {
                throw new InvalidOperationException(
                    $"Ops:NodeTokenHosts contains '{NodeTokenHosts[index]}', which is not a bare host name or IP address (no scheme, port or path).");
            }

            NodeTokenHosts[index] = host;
        }

        Retention.Validate();
    }
}

/// <summary>Retention windows, in days. Zero disables the delete for that table.</summary>
public sealed class RetentionSettings
{
    /// <summary>Raw metric samples. The overview reads minutes, not months.</summary>
    public int SampleDays { get; set; } = 30;

    /// <summary>
    /// Downsampled one-minute and one-hour metric buckets. Zero by default: <see cref="OpsOptions.RollupEnabled"/> is
    /// also off, so there is nothing new to retain. Enable both together when a trend view reads the buckets; the
    /// window is then usually longer than raw samples so useful history survives without every ten-second scrape.
    /// </summary>
    public int RollupDays { get; set; }

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
        if (SampleDays < 0 || RollupDays < 0 || EventDays < 0 || SessionDays < 0 || AlertDays < 0)
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
/// Outgoing notifications. One webhook and one mailbox for the whole console rather than a channel table: the common
/// receiver is a group-chat robot, the URL is the whole configuration, and a table with no UI would be a table nobody
/// fills in. The two channels are independent — one failing does not stop the other, and each keeps its own retries.
/// </summary>
public sealed class NotificationSettings
{
    /// <summary>Receives a JSON POST when an alert opens or resolves. Empty disables notifications.</summary>
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>Mail delivery of the same alerts. Empty <see cref="SmtpSettings.Host"/> disables it.</summary>
    public SmtpSettings Smtp { get; set; } = new();
}

/// <summary>How the console sends an alert by mail.</summary>
/// <remarks>
/// The password is read from configuration and only ever written into the AUTH exchange: it is not logged, not put in
/// an error message and not stored on the attempt row (which keeps the server's reason for refusing, nothing more).
/// </remarks>
public sealed class SmtpSettings
{
    /// <summary>Empty disables mail delivery entirely.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>587 (submission), 465 for implicit TLS (not supported — use STARTTLS) or 25 on an internal relay.</summary>
    public int Port { get; set; } = 587;

    /// <summary>Upgrade the connection with STARTTLS before AUTH. Off only on a trusted network.</summary>
    public bool UseStartTls { get; set; } = true;

    /// <summary>Empty means the relay needs no authentication.</summary>
    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>Envelope and header sender. Required when <see cref="Host"/> is set.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>One or more recipients, separated by commas or semicolons.</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>Whether mail delivery was configured at all.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);

    /// <summary>The recipients, trimmed, with the empty entries a trailing comma leaves behind removed.</summary>
    public IReadOnlyList<string> Recipients() =>
        [.. To.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>Refuses a half-written configuration, naming the key that has to change.</summary>
    /// <exception cref="InvalidOperationException">A value cannot be used.</exception>
    public void Validate()
    {
        if (!IsConfigured)
        {
            if (!string.IsNullOrWhiteSpace(From) || !string.IsNullOrWhiteSpace(To) || !string.IsNullOrWhiteSpace(Username))
            {
                throw new InvalidOperationException(
                    "Ops:Notifications:Smtp:Host is required as soon as any other Ops:Notifications:Smtp value is set (empty Host disables mail delivery).");
            }

            return;
        }

        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"Ops:Notifications:Smtp:Port must be between 1 and 65535 (got {Port}).");
        }

        if (!LooksLikeAddress(From))
        {
            throw new InvalidOperationException(
                "Ops:Notifications:Smtp:From must be one mail address, for example ops@example.com.");
        }

        var recipients = Recipients();
        if (recipients.Count == 0)
        {
            throw new InvalidOperationException("Ops:Notifications:Smtp:To must name at least one recipient.");
        }

        foreach (var recipient in recipients)
        {
            if (!LooksLikeAddress(recipient))
            {
                throw new InvalidOperationException($"Ops:Notifications:Smtp:To contains '{recipient}', which is not a mail address.");
            }
        }

        if (string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password))
        {
            throw new InvalidOperationException(
                "Ops:Notifications:Smtp:Password is set but Ops:Notifications:Smtp:Username is empty; an unauthenticated relay cannot use one.");
        }
    }

    // Deliberately not a full RFC 5322 parse: this only has to catch the mistakes that would otherwise fail at
    // delivery time, on the one path whose failure nobody sees.
    private static bool LooksLikeAddress(string? value)
    {
        var at = value?.IndexOf('@', StringComparison.Ordinal) ?? -1;
        return !string.IsNullOrWhiteSpace(value)
            && at > 0
            && at < value.Length - 1
            && !value.Contains(' ', StringComparison.Ordinal)
            && !value.Contains('\r', StringComparison.Ordinal)
            && !value.Contains('\n', StringComparison.Ordinal);
    }
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
