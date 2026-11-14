using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace RemoteDesktop.Server.Shared;

/// <summary>What the installer was told, on its way to the two servers' settings files. Empty means "leave it".</summary>
/// <remarks>
/// The values come from command line arguments the installer passes, so they are what somebody typed — or what a
/// property in the package defaulted to. <see cref="Auto"/> is the one word with a meaning of its own, described on
/// each property that accepts it.
/// </remarks>
public sealed record ServerSetupOptions
{
    /// <summary>The value that says "decide for me".</summary>
    public const string Auto = "auto";

    /// <summary>
    /// TlsCertificatePath value that means "make one for this machine" instead of naming a file: a self-signed
    /// certificate, created on the spot and kept in the machine's certificate store. See <see cref="SelfSignedCertificate"/>.
    /// </summary>
    public const string GenerateCertificate = "generate";

    /// <summary>What the signaling server listens on, e.g. <c>http://0.0.0.0:5080</c> or <c>https://0.0.0.0:5080</c>.</summary>
    public string? Urls { get; init; }

    /// <summary>The address clients are told to relay through, as <c>host:port</c> — what they can reach, not necessarily what this machine calls itself.</summary>
    public string? RelayPublicEndpoint { get; init; }

    /// <summary>
    /// The base64 key that signs relay tickets, shared by both servers. <see cref="Auto"/> keeps the key this machine
    /// already has and generates one when there is none; a key that is already configured is never replaced, because a
    /// relay running on another machine was set up with the one that was printed when it was installed.
    /// </summary>
    public string? RelaySharedKey { get; init; }

    /// <summary>
    /// Where device codes are kept: <c>auto</c>, <c>none</c>, <c>postgres</c> or <c>sqlite</c>.
    /// <see cref="ServerSetupOptions.Auto"/> keeps PostgreSQL when the machine already has a connection string and
    /// otherwise starts a SQLite database in the platform's data folder — the reading every other <c>auto</c> in this
    /// file gets, "decide for this machine". Null means the caller said nothing, and then the database settings are
    /// left exactly as they are.
    /// </summary>
    public string? DatabaseProvider { get; init; }

    /// <summary>The SQLite file. Empty means the platform's default, and is only read when the provider is sqlite.</summary>
    public string? DatabasePath { get; init; }

    /// <summary>
    /// Mounts <c>/metrics</c> on the signaling server. The installer turns this on when it also installs the console:
    /// the console scrapes that endpoint for its node page, and a node it cannot scrape is a row that reads "failed"
    /// from the first second. The endpoint is still protected — loopback, or the shared node token.
    /// </summary>
    public bool MetricsEnabled { get; init; }

    /// <summary>
    /// PostgreSQL connection string, as the installers spelled it before there was a provider setting. A value here
    /// still means PostgreSQL; an empty one means no database. Superseded by <see cref="DatabaseProvider"/> plus
    /// <see cref="DatabasePath"/>, and kept because every existing settings file and every installer property is
    /// written this way.
    /// </summary>
    public string? PostgresConnectionString { get; init; }

    /// <summary>A .pfx the signaling server should serve TLS with. Empty leaves it on plain http.</summary>
    public string? TlsCertificatePath { get; init; }

    public string? TlsCertificatePassword { get; init; }

    /// <summary>
    /// A DNS name (or comma/semicolon-separated names) clients reach this server through. Only used when the
    /// certificate is generated: a self-signed certificate only answers the names that were put in it.
    /// </summary>
    public string? TlsHostName { get; init; }

    /// <summary>Where the monitoring console keeps its SQLite file. Empty means the platform's default.</summary>
    public string? OpsDatabasePath { get; init; }

    /// <summary>Where the monitoring console listens. Empty leaves whatever the settings already say.</summary>
    public string? OpsListen { get; init; }

    /// <summary>
    /// Where Rendezvous and Relay send session events. <see cref="Auto"/> keeps an existing endpoint and otherwise
    /// derives a loopback endpoint from <see cref="OpsListen"/>. An explicit value is needed by a relay on another machine.
    /// </summary>
    public string? OpsEventsEndpoint { get; init; }

    /// <summary>
    /// The base64 HMAC key shared by Rendezvous, Relay and the console. <see cref="Auto"/> keeps existing keys and
    /// generates one only when this machine has none. An explicit value synchronizes the well-known <c>k1</c> key.
    /// </summary>
    public string? OpsIngestKey { get; init; }

    /// <summary>
    /// A certificate the console pins when it calls its nodes (scrapes and the device proxy). Empty means the system
    /// trust store; a self-signed signaling certificate cannot be trusted that way from an installer script.
    /// </summary>
    public string? OpsNodeCertificatePath { get; init; }

    /// <summary>The public HTTPS address of the Ops reverse proxy. Null leaves the existing listener unchanged.</summary>
    public string? GatewayListen { get; init; }

    /// <summary>Extra DNS names or IP addresses the generated Gateway certificate must contain.</summary>
    public string? GatewayHostName { get; init; }
}

/// <summary>
/// The steps the server installer runs that are not file copying: pointing the servers at each other and at the
/// console, giving them shared keys, and configuring TLS. Run through
/// <c>RemoteDesktop.Server.Rendezvous.exe --configure-system</c>; every step is safe to repeat.
/// </summary>
/// <remarks>
/// The settings files are read, changed and written back rather than replaced, so anything an operator edited by hand
/// — a log level, an extra setting — survives an upgrade, and a key that is already in place is never rotated.
/// </remarks>
public static partial class ServerSetup
{
    /// <summary>Where the Windows installer keeps the live settings: <c>%ProgramData%\RemoteDesktopServer</c>.</summary>
    /// <remarks>
    /// The program folders stay under Program Files; the settings do not. A major upgrade removes the old product's
    /// files before installing the new ones, so anything the MSI owns under Program Files is deleted on every upgrade.
    /// The live settings have to live where the MSI does not manage them. Linux uses <c>/etc/remotedesktop-server</c>
    /// and macOS keeps them next to the programs because its <c>.pkg</c> does not remove files it did not install;
    /// ProgramData is the Windows equivalent.
    /// </remarks>
    public static string WindowsConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "RemoteDesktopServer");

    /// <summary>The folder each server keeps its settings in, under the configuration directory.</summary>
    public const string RendezvousFolder = "Rendezvous";

    public const string RelayFolder = "Relay";

    /// <summary>The monitoring console's folder, which has settings of its own.</summary>
    public const string OpsFolder = "Ops";

    /// <summary>The HTTPS reverse proxy that exposes the console without opening its loopback listener.</summary>
    public const string GatewayFolder = "Gateway";

    public const string DefaultGatewayListen = "https://0.0.0.0:5443";

    public const string DefaultGatewayUpstream = "http://127.0.0.1:5090";

    /// <summary>Where the Linux package keeps the live settings for all three servers.</summary>
    public const string LinuxConfigDirectory = "/etc/remotedesktop-server";

    public const string SettingsFileName = "appsettings.json";

    /// <summary>
    /// The directory a setup command reads for <paramref name="roleFolder"/>, matching where the installed service
    /// reads it.
    /// </summary>
    /// <remarks>
    /// Installed layouts differ from a development or container run. On Windows the package puts the program under
    /// Program Files and the live settings in ProgramData; a repository build lives under <c>artifacts/bin</c> and
    /// must read the settings beside itself, not the production database of an installed console on the same machine.
    /// Linux packages install under <c>/usr/lib/remotedesktop-server</c> and read <c>/etc/remotedesktop-server</c>;
    /// Docker's <c>/app</c> and a dev build read beside the program. macOS keeps settings beside the executable in
    /// both cases.
    /// </remarks>
    public static string DefaultConfigDirectory(string roleFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleFolder);

        var programDirectory = Path.GetFullPath(AppContext.BaseDirectory);

        if (OperatingSystem.IsWindows() && IsUnder(programDirectory, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)))
        {
            return Path.Combine(WindowsConfigDirectory, roleFolder);
        }

        if (OperatingSystem.IsLinux()
            && IsUnder(programDirectory, "/usr/lib/remotedesktop-server"))
        {
            return Path.Combine(LinuxConfigDirectory, roleFolder);
        }

        return programDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsUnder(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var prefix = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The default signaling port, used when no URL is given.</summary>
    public const int DefaultSignalingPort = 5080;

    /// <summary>The port the relay listens on when nothing else is said.</summary>
    public const int DefaultRelayPort = 5082;

    private const int MinimumKeyBytes = 32;

    private const string DefaultOpsKeyId = "k1";

    private const string DefaultOpsEventsEndpoint = "http://127.0.0.1:5090";

    /// <summary>The section the provider and its settings live in, nested under <c>Rendezvous</c>.</summary>
    private const string DatabaseSectionName = "Database";

    /// <summary>What earlier versions wrote instead of a provider. Read, and still written while it means the same.</summary>
    private const string LegacyPostgresKey = "PostgresConnectionString";

    private const string ProviderNone = "none";

    private const string ProviderPostgres = "postgres";

    private const string ProviderSqlite = "sqlite";

    /// <summary>
    /// Writes the live settings under <paramref name="configDirectory"/>.
    /// </summary>
    /// <param name="configDirectory">
    /// The directory holding <c>Rendezvous</c>, <c>Relay</c> and <c>Ops</c>. On Linux and macOS this is also the
    /// directory the servers run from; on Windows it is <see cref="WindowsConfigDirectory"/>.
    /// </param>
    /// <param name="options">What the installer or the operator asked for.</param>
    /// <param name="defaultsDirectory">
    /// Where to read the shipped <c>appsettings.json</c> from when a live file does not exist yet — the program
    /// directory on Windows. Null means the live directory itself (the Linux and macOS layout).
    /// </param>
    /// <returns>What happened, one line at a time, for whoever is watching the installation.</returns>
    /// <exception cref="InvalidOperationException">A value cannot be used; nothing is written in that case.</exception>
    public static IReadOnlyList<string> Configure(
        string configDirectory,
        ServerSetupOptions options,
        string? defaultsDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        ArgumentNullException.ThrowIfNull(options);

        var notes = new List<string>();
        var rendezvousPath = Path.Combine(configDirectory, RendezvousFolder, SettingsFileName);
        var relayPath = Path.Combine(configDirectory, RelayFolder, SettingsFileName);
        var opsPath = Path.Combine(configDirectory, OpsFolder, SettingsFileName);
        var gatewayPath = Path.Combine(configDirectory, GatewayFolder, SettingsFileName);
        var gatewayDefaultsPath = DefaultsPath(defaultsDirectory, GatewayFolder);

        // Read every file before writing any: a value that turns out to be unusable should leave the machine as it was.
        // A live file wins; otherwise the shipped file is the base, so a fresh Windows install keeps the template's
        // Urls/ReflectorPort rather than starting from an empty object.
        var rendezvous = ReadOrDefault(rendezvousPath, DefaultsPath(defaultsDirectory, RendezvousFolder));
        var relay = ReadOrDefault(relayPath, DefaultsPath(defaultsDirectory, RelayFolder));
        var ops = ReadOrDefault(opsPath, DefaultsPath(defaultsDirectory, OpsFolder));
        var configureGateway = options.GatewayListen is not null
            || options.GatewayHostName is not null
            || File.Exists(gatewayPath)
            || gatewayDefaultsPath is not null && File.Exists(gatewayDefaultsPath);
        var gateway = configureGateway ? ReadOrDefault(gatewayPath, gatewayDefaultsPath) : [];

        var rendezvousSection = Section(rendezvous, "Rendezvous");
        var (endpoint, relayPort) = RelayEndpoint(options.RelayPublicEndpoint, notes);
        var key = SharedKey(options.RelaySharedKey, rendezvous, relay, notes);
        var urls = SignalingUrl(options.Urls, Text(rendezvous, "Urls"), options.TlsCertificatePath, notes);

        // "Not given" is not "set to the default": configure-server.sh and an upgrade pass only what changed, and
        // rewriting the address or the relay endpoint here would silently undo what an operator configured months
        // earlier. A fresh installation gets the defaults because the installers pass auto explicitly.
        if (urls is not null)
        {
            rendezvous["Urls"] = urls;
        }

        // A freshly configured server listens on loopback unless the operator chose a public plaintext address.
        // Record that choice explicitly so the server's default-deny check does not make an existing deployment stop
        // after this upgrade, while a hand-written public http:// address still has to opt in itself.
        var effectiveUrls = Text(rendezvous, "Urls");
        // Urls can hold several addresses and wildcard hosts; any public plaintext one is an opt-in.
        rendezvousSection["AllowInsecureSignaling"] = ListeningUrl.HasPublicPlaintext(effectiveUrls);

        if (endpoint is not null)
        {
            rendezvousSection["RelayEndpoint"] = endpoint;
        }

        rendezvousSection["RelaySharedKey"] = key;
        if (options.MetricsEnabled)
        {
            // Only ever switched on here. Absent means "whatever this file already said", so a reconfigure that does
            // not mention metrics cannot silently take monitoring away from a deployment that turned it on by hand.
            rendezvousSection["MetricsEnabled"] = true;
        }

        var database = ConfigureDatabase(rendezvousSection, options, notes);
        var opsConfigured = ConfigureOps(ops, database, options, notes);
        var opsEventsConfigured = ConfigureOpsEvents(rendezvous, relay, ops, options, notes);

        // A password that was not given is not "the certificate has no password": configure-server.sh and the
        // installer's upgrade pane leave the password field empty because they do not store it. If the path did not
        // change, the password already on the machine is the one to use — otherwise a reconfiguration would clear it
        // and the server would fail to start on its own PFX.
        var existingCertificates = rendezvous["Kestrel"]?["Certificates"]?["Default"] as JsonObject;
        var existingPath = existingCertificates?["Path"]?.GetValue<string>();
        var existingPassword = existingCertificates?["Password"]?.GetValue<string>();
        var certificatePassword = options.TlsCertificatePassword;
        if (IsAuto(certificatePassword)
            && !IsAuto(options.TlsCertificatePath)
            && options.TlsCertificatePath != ServerSetupOptions.GenerateCertificate
            && string.Equals(existingPath, options.TlsCertificatePath, StringComparison.Ordinal))
        {
            certificatePassword = existingPassword;
        }

        // A certificate file that cannot actually be loaded has to fail here, while somebody is watching, rather than
        // leave an installation whose service dies on its first start with a message about Kestrel.
        if (!IsAuto(options.TlsCertificatePath)
            && options.TlsCertificatePath != ServerSetupOptions.GenerateCertificate
            && urls is not null
            && urls.StartsWith("https://", StringComparison.Ordinal))
        {
            ValidateCertificateFile(options.TlsCertificatePath!, certificatePassword);
        }

        // Signaling and the Ops gateway share the machine certificate. Build its SAN list once so the first caller
        // cannot create a certificate that omits the address the second listener needs.
        var additionalNames = AdditionalNames(options.TlsHostName)
            .Concat(AdditionalNames(options.GatewayHostName))
            .ToArray();

        if (!IsAuto(options.TlsCertificatePath)
            && urls is not null
            && urls.StartsWith("https://", StringComparison.Ordinal))
        {
            var certificates = Section(rendezvous, "Kestrel");
            certificates = Section(certificates, "Certificates");
            certificates = Section(certificates, "Default");

            if (options.TlsCertificatePath == ServerSetupOptions.GenerateCertificate)
            {
                // Whatever an earlier run left here is dropped first, or it would win over what is made now: a path
                // from a file-based run, or the store entry the Windows form writes.
                foreach (var stale in (string[])["Path", "KeyPath", "Password", "Subject", "Store", "Location", "AllowInvalid"])
                {
                    certificates.Remove(stale);
                }

                if (OperatingSystem.IsWindows())
                {
                    // Made during this run and kept in the machine's store, so the settings name it rather than point
                    // at a file. What Ensure returns is the subject with the CN= on the front, which is not what goes
                    // into the settings: Kestrel looks a certificate up by subject *name*, so the prefixed form finds
                    // nothing. See NameForThisMachine.
                    SelfSignedCertificate.Ensure(configDirectory, notes, additionalNames);
                    certificates["Subject"] = SelfSignedCertificate.NameForThisMachine;
                    certificates["Store"] = SelfSignedCertificate.CertificateStoreName;
                    certificates["Location"] = SelfSignedCertificate.CertificateStoreLocation;

                    // Without this the server does not start at all. Kestrel looks a certificate up in the store with
                    // "valid only" unless told otherwise, and a self-signed certificate is not in this machine's
                    // trusted roots — it is not in anybody's — so the lookup finds nothing and the host stops with
                    // "The requested certificate <subject> could not be found in LocalMachine/My with AllowInvalid
                    // setting: False", a sentence about a setting rather than about the reason. Trusting it is the
                    // client's business; serving it is the server's, and this is what lets it.
                    certificates["AllowInvalid"] = "true";
                }
                else
                {
                    // Nowhere to keep a certificate on this side, so both halves are files and Kestrel is pointed at
                    // them. Kestrel reads a PEM certificate from Path and its key from KeyPath; there is no
                    // equivalent of AllowInvalid because a certificate given as a file is served as it stands.
                    var (certificatePath, keyPath) = SelfSignedCertificate.EnsurePem(
                        Path.Combine(configDirectory, SelfSignedCertificate.PublicFolder),
                        notes,
                        additionalNames);

                    certificates["Path"] = certificatePath;
                    certificates["KeyPath"] = keyPath;
                }

                notes.Add("Signaling is served over TLS with the certificate this step created.");
            }
            else
            {
                // A certificate file replaces whatever the previous run used: a PEM pair's KeyPath, or the Windows
                // store's Subject/Store/Location/AllowInvalid. Leaving the old KeyPath behind makes Kestrel load the
                // new certificate from Path but its private key from the old file, and the server dies at startup
                // with "the provided key file is missing or invalid". Only the keys that are re-added below survive.
                foreach (var stale in (string[])["KeyPath", "Subject", "Store", "Location", "AllowInvalid"])
                {
                    certificates.Remove(stale);
                }

                // Not "auto" here, but the compiler cannot know it across the IsAuto call above.
                var certificatePath = options.TlsCertificatePath!;
                certificates["Path"] = certificatePath;
                certificates["Password"] = IsAuto(certificatePassword) ? string.Empty : certificatePassword!;

                // A PEM certificate carries no password: its key is a second file, and Kestrel is pointed at it
                // separately. Looking for the key beside the certificate under the same name is the convention
                // everything else on the system follows, so an operator who already has a pair has nothing to
                // configure beyond the certificate's path.
                if (!OperatingSystem.IsWindows() && KeyBeside(certificatePath) is { } keyPath)
                {
                    certificates["KeyPath"] = keyPath;
                    certificates.Remove("Password");
                    notes.Add($"Signaling is served over TLS with {certificatePath} and the key beside it ({keyPath}).");
                }
                else
                {
                    notes.Add($"Signaling is served over TLS with {certificatePath}.");
                }
            }
        }

        var relaySection = Section(relay, "Relay");
        if (relayPort is not null)
        {
            relaySection["Port"] = relayPort.Value;
        }

        relaySection["SharedKey"] = key;

        var gatewayOpsConfigured = configureGateway
            && ConfigureGateway(gateway, ops, options, configDirectory, additionalNames, notes);

        Write(rendezvousPath, rendezvous);
        Write(relayPath, relay);
        if (configureGateway)
        {
            Write(gatewayPath, gateway);
        }

        // A first install and an upgrade from a version without event reporting both have console settings to write.
        // Later runs leave the file alone when neither its database nor its event keys changed.
        if (opsConfigured || opsEventsConfigured || gatewayOpsConfigured)
        {
            Write(opsPath, ops);
        }

        notes.Add(urls is null
            ? "The signaling address was left unchanged."
            : $"Signaling listens on {urls}.");
        notes.Add(endpoint is null
            ? "The relay address was left unchanged."
            : $"Relay listens on port {relayPort}; clients are told to use {endpoint}.");
        return notes;
    }

    /// <summary>
    /// The address clients are told to relay through.
    /// </summary>
    /// <remarks>
    /// It has to be an address and not a name: the client takes what it is given and sends UDP there, so a name would
    /// fail on the client's side with nothing useful to say. A name is therefore resolved here, once, at install
    /// time — the trade is that the address is then fixed, so a machine whose address changes needs this run again.
    /// </remarks>
    private static (string? Endpoint, int? Port) RelayEndpoint(string? configured, List<string> notes)
    {
        // Null means the caller said nothing about the relay address; an empty or "auto" value means
        // "decide for this machine". Keeping those apart is what lets an upgrade or configure-server.sh change one
        // setting without silently moving every client to the build machine's first non-loopback address.
        if (configured is null)
        {
            return (null, null);
        }

        var endpoint = Expand(configured);
        if (IsAuto(endpoint))
        {
            var local = LocalAddress();
            notes.Add($"No relay address was given, so clients are told to use {local}:{DefaultRelayPort}. Change Rendezvous:RelayEndpoint in the settings file if that is not what they can reach.");
            return ($"{local}:{DefaultRelayPort}", DefaultRelayPort);
        }

        if (!TrySplit(endpoint, out var host, out var port))
        {
            throw new InvalidOperationException($"The relay address must look like address:port (got '{endpoint}').");
        }

        if (IPAddress.TryParse(host, out var address))
        {
            return ($"{address}:{port}", port);
        }

        var resolved = Resolve(host);
        if (resolved is null)
        {
            throw new InvalidOperationException($"The relay address '{endpoint}' is a name that could not be resolved. Clients are sent straight to this address, so it has to be one they can reach: give an address, or a name that resolves here.");
        }

        notes.Add($"The relay address '{host}' was resolved to {resolved} at install time. Clients are sent straight to that address, so run this step again if it changes.");
        return ($"{resolved}:{port}", port);

        static bool TrySplit(string value, out string host, out int port)
        {
            host = string.Empty;
            port = DefaultRelayPort;
            var separator = value.LastIndexOf(':');
            return separator > 0
                && int.TryParse(value[(separator + 1)..], out port)
                && port is > 0 and <= ushort.MaxValue
                && (host = value[..separator]).Length > 0;
        }
    }

    /// <summary>This machine's first address that is not loopback, or loopback when it has none.</summary>
    private static IPAddress LocalAddress()
    {
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var address in network.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
                {
                    return address.Address;
                }
            }
        }

        return IPAddress.Loopback;
    }

    private static IPAddress? Resolve(string host)
    {
        try
        {
            return Dns.GetHostAddresses(host).FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The key both servers sign and check relay tickets with. Generated once and then kept: a relay on another
    /// machine was given the key this installation printed, and rotating it would quietly cut that relay off.
    /// </summary>
    private static string SharedKey(string? configured, JsonObject rendezvous, JsonObject relay, List<string> notes)
    {
        var key = Expand(configured);
        if (!IsAuto(key))
        {
            ValidateKey(key);
            return key;
        }

        var existing = ExistingKey(rendezvous) ?? ExistingKey(relay);
        if (existing is not null)
        {
            notes.Add("The relay shared key already on this machine was kept.");
            return existing;
        }

        var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        notes.Add("A relay shared key was generated. Copy it to any relay installed on another machine (Rendezvous:RelaySharedKey / Relay:SharedKey).");
        return generated;

        static string? ExistingKey(JsonObject settings) =>
            settings["Relay"]?["SharedKey"]?.GetValue<string>() is { Length: > 0 } fromRelay
                ? fromRelay
                : settings["Rendezvous"]?["RelaySharedKey"]?.GetValue<string>() is { Length: > 0 } fromRendezvous
                    ? fromRendezvous
                    : null;
    }

    /// <summary>A value the caller gave, with the difference between "not given" (null) and "given as empty" kept.</summary>
    private static string? Given(string? value) => value?.Trim();

    private static string? FirstGiven(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? Text(JsonObject? settings, string name) =>
        settings?[name]?.GetValue<string>() is { Length: > 0 } value ? value : null;

    /// <summary>
    /// The same bar the servers themselves apply at start-up, checked here so a key that would stop them from
    /// starting is refused while somebody is still watching the installation.
    /// </summary>
    private static void ValidateKey(string key) =>
        Base64Key.Decode(
            key,
            "The relay shared key must be base64 (generate one with: openssl rand -base64 48).",
            length => $"The relay shared key must be at least {MinimumKeyBytes} bytes ({length} given). Generate one with: openssl rand -base64 48",
            MinimumKeyBytes);

    private static JsonObject Section(JsonObject settings, string name)
    {
        if (settings[name] is JsonObject existing)
        {
            return existing;
        }

        var section = new JsonObject();
        settings[name] = section;
        return section;
    }

    private static bool IsAuto(string? value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), ServerSetupOptions.Auto, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Environment variables are expanded so that a value like <c>%COMPUTERNAME%:5082</c> works: it can only be
    /// resolved on the machine being installed, which is where this runs. The package's own default for that setting
    /// is <c>auto</c> rather than this, because a name that does not resolve would fail the whole step.
    /// </summary>
    private static string Expand(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : Environment.ExpandEnvironmentVariables(value.Trim());
}
