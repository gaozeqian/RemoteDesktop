using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    /// <summary>Where device codes are kept. <see cref="Auto"/> means no database — see the warning in the notes.</summary>
    public string? PostgresConnectionString { get; init; }

    /// <summary>A .pfx the signaling server should serve TLS with. Empty leaves it on plain http.</summary>
    public string? TlsCertificatePath { get; init; }

    public string? TlsCertificatePassword { get; init; }
}

/// <summary>
/// The steps the server installer runs that are not file copying: pointing the two servers at each other, giving them
/// a shared key, and configuring TLS. Run through
/// <c>RemoteDesktop.Server.Rendezvous.exe --configure-system</c>; every step is safe to repeat.
/// </summary>
/// <remarks>
/// The settings files are read, changed and written back rather than replaced, so anything an operator edited by hand
/// — a log level, an extra setting — survives an upgrade, and a key that is already in place is never rotated.
/// </remarks>
public static class ServerSetup
{
    /// <summary>The folder each server keeps its settings next to, under the install directory.</summary>
    public const string RendezvousFolder = "Rendezvous";

    public const string RelayFolder = "Relay";

    public const string SettingsFileName = "appsettings.json";

    /// <summary>The default signaling port, used when no URL is given.</summary>
    public const int DefaultSignalingPort = 5080;

    /// <summary>The port the relay listens on when nothing else is said.</summary>
    public const int DefaultRelayPort = 5082;

    private const int MinimumKeyBytes = 32;

    /// <summary>
    /// Writes both settings files under <paramref name="installDirectory"/>.
    /// </summary>
    /// <returns>What happened, one line at a time, for whoever is watching the installation.</returns>
    /// <exception cref="InvalidOperationException">A value cannot be used; nothing is written in that case.</exception>
    public static IReadOnlyList<string> Configure(string installDirectory, ServerSetupOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentNullException.ThrowIfNull(options);

        var notes = new List<string>();
        var rendezvousPath = Path.Combine(installDirectory, RendezvousFolder, SettingsFileName);
        var relayPath = Path.Combine(installDirectory, RelayFolder, SettingsFileName);

        // Read both before writing either: a value that turns out to be unusable should leave the machine as it was.
        var rendezvous = Read(rendezvousPath);
        var relay = Read(relayPath);

        var (endpoint, relayPort) = RelayEndpoint(options.RelayPublicEndpoint, notes);
        var key = SharedKey(options.RelaySharedKey, rendezvous, relay, notes);
        var urls = SignalingUrl(options.Urls, options.TlsCertificatePath, notes);
        var postgres = Expand(options.PostgresConnectionString);

        var rendezvousSection = Section(rendezvous, "Rendezvous");
        rendezvous["Urls"] = urls;
        rendezvousSection["RelayEndpoint"] = endpoint;
        rendezvousSection["RelaySharedKey"] = key;
        rendezvousSection["PostgresConnectionString"] = IsAuto(postgres) ? string.Empty : postgres;

        if (!IsAuto(options.TlsCertificatePath) && urls.StartsWith("https://", StringComparison.Ordinal))
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
                    SelfSignedCertificate.Ensure(installDirectory, notes);
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
                        Path.Combine(installDirectory, SelfSignedCertificate.PublicFolder),
                        notes);

                    certificates["Path"] = certificatePath;
                    certificates["KeyPath"] = keyPath;
                }

                notes.Add("Signaling is served over TLS with the certificate this step created.");
            }
            else
            {
                // Not "auto" here, but the compiler cannot know it across the IsAuto call above.
                var certificatePath = options.TlsCertificatePath!;
                certificates["Path"] = certificatePath;
                certificates["Password"] = IsAuto(options.TlsCertificatePassword) ? string.Empty : options.TlsCertificatePassword!;

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
        relaySection["Port"] = relayPort;
        relaySection["SharedKey"] = key;

        Write(rendezvousPath, rendezvous);
        Write(relayPath, relay);

        notes.Add($"Signaling listens on {urls}.");
        notes.Add($"Relay listens on port {relayPort}; clients are told to use {endpoint}.");
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
    private static (string Endpoint, int Port) RelayEndpoint(string? configured, List<string> notes)
    {
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

    /// <summary>
    /// What the signaling server listens on. A certificate switches it to https on its own: an installer that was
    /// given one was asked for TLS, and asking for TLS and serving http is not something to do quietly.
    /// </summary>
    private static string SignalingUrl(string? configured, string? certificatePath, List<string> notes)
    {
        var urls = Expand(configured);
        if (IsAuto(urls))
        {
            urls = $"http://0.0.0.0:{DefaultSignalingPort}";
        }

        if (urls.StartsWith("https://", StringComparison.Ordinal))
        {
            if (IsAuto(certificatePath))
            {
                notes.Add("The signaling address is https but no certificate was given: the server will not start until Kestrel:Certificates:Default is filled in by hand.");
            }

            return urls;
        }

        if (!urls.StartsWith("http://", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The signaling address must start with http:// or https:// (got '{urls}').");
        }

        // "auto" is the installer's default property value, and it means the same as an empty one here: no certificate.
        if (IsAuto(certificatePath))
        {
            notes.Add("Signaling is served over plain http: anyone on the way can read the device codes and take a relay ticket. Give the installer a certificate (see docs/ops/deployment.md) or put a reverse proxy in front.");
            return urls;
        }

        if (certificatePath != ServerSetupOptions.GenerateCertificate && !File.Exists(certificatePath))
        {
            notes.Add($"The certificate '{certificatePath}' is not there, so the server was left on plain http. Put the file in place and set Kestrel:Certificates:Default by hand, or run this step again.");
            return urls;
        }

        // Either a file that is there, or the certificate this step is about to make.
        return "https://" + urls["http://".Length..];
    }

    /// <summary>
    /// The same bar the servers themselves apply at start-up, checked here so a key that would stop them from
    /// starting is refused while somebody is still watching the installation.
    /// </summary>
    private static void ValidateKey(string key)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(key);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("The relay shared key must be base64 (generate one with: openssl rand -base64 48).");
        }

        if (bytes.Length < MinimumKeyBytes)
        {
            throw new InvalidOperationException($"The relay shared key must be at least {MinimumKeyBytes} bytes ({bytes.Length} given). Generate one with: openssl rand -base64 48");
        }
    }

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

    private static JsonObject Read(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            // Comments are allowed: the settings files in the repository carry some, and an operator may add more.
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }) as JsonObject ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{path} is not valid JSON, so it was left alone: {ex.Message}");
        }
    }

    /// <summary>
    /// The private key that belongs to a PEM certificate, if it is beside it.
    /// </summary>
    /// <returns><c>null</c> for a certificate that is not PEM, or whose key is not where it would be expected.</returns>
    private static string? KeyBeside(string certificatePath)
    {
        if (Path.GetExtension(certificatePath) is not (".crt" or ".pem" or ".cer"))
        {
            return null;
        }

        var keyPath = Path.ChangeExtension(certificatePath, ".key");
        return File.Exists(keyPath) ? keyPath : null;
    }

    private static void Write(string path, JsonObject settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Written beside the file and moved into place: an installation that is interrupted must not leave half a
        // settings file behind, because a server that cannot read its settings does not start.
        //
        // The encoder is relaxed so that what is written can be read: the default escapes a "+" as +, and the
        // base64 relay key is full of them. Inside a JSON string that still means the same key, but the notes tell an
        // operator to copy that key to a relay on another machine — and on a command line nothing decodes +, so
        // the relay would be given a different key and refuse every ticket.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, settings.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        File.Move(temporary, path, overwrite: true);
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
