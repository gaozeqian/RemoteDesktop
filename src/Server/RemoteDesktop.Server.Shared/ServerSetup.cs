using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

    /// <summary>The monitoring console's folder, which has settings of its own.</summary>
    public const string OpsFolder = "Ops";

    public const string SettingsFileName = "appsettings.json";

    /// <summary>The default signaling port, used when no URL is given.</summary>
    public const int DefaultSignalingPort = 5080;

    /// <summary>The port the relay listens on when nothing else is said.</summary>
    public const int DefaultRelayPort = 5082;

    private const int MinimumKeyBytes = 32;

    /// <summary>The section the provider and its settings live in, nested under <c>Rendezvous</c>.</summary>
    private const string DatabaseSectionName = "Database";

    /// <summary>What earlier versions wrote instead of a provider. Read, and still written while it means the same.</summary>
    private const string LegacyPostgresKey = "PostgresConnectionString";

    private const string ProviderNone = "none";

    private const string ProviderPostgres = "postgres";

    private const string ProviderSqlite = "sqlite";

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
        var opsPath = Path.Combine(installDirectory, OpsFolder, SettingsFileName);

        // Read all three before writing any: a value that turns out to be unusable should leave the machine as it was.
        var rendezvous = Read(rendezvousPath);
        var relay = Read(relayPath);
        var ops = Read(opsPath);

        var (endpoint, relayPort) = RelayEndpoint(options.RelayPublicEndpoint, notes);
        var key = SharedKey(options.RelaySharedKey, rendezvous, relay, notes);
        var urls = SignalingUrl(options.Urls, options.TlsCertificatePath, notes);

        var rendezvousSection = Section(rendezvous, "Rendezvous");
        rendezvous["Urls"] = urls;
        rendezvousSection["RelayEndpoint"] = endpoint;
        rendezvousSection["RelaySharedKey"] = key;
        if (options.MetricsEnabled)
        {
            // Only ever switched on here. Absent means "whatever this file already said", so a reconfigure that does
            // not mention metrics cannot silently take monitoring away from a deployment that turned it on by hand.
            rendezvousSection["MetricsEnabled"] = true;
        }

        var database = ConfigureDatabase(rendezvousSection, options, notes);
        var opsConfigured = ConfigureOps(ops, database, options, notes);

        // A certificate file that cannot actually be loaded has to fail here, while somebody is watching, rather than
        // leave an installation whose service dies on its first start with a message about Kestrel.
        if (!IsAuto(options.TlsCertificatePath)
            && options.TlsCertificatePath != ServerSetupOptions.GenerateCertificate
            && urls.StartsWith("https://", StringComparison.Ordinal))
        {
            ValidateCertificateFile(options.TlsCertificatePath!, options.TlsCertificatePassword);
        }

        var additionalNames = AdditionalNames(options.TlsHostName);

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
                    SelfSignedCertificate.Ensure(installDirectory, notes, additionalNames);
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
                        notes,
                        additionalNames);

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

        // The console's settings are only touched when somebody said something about the database or its address.
        // The Windows installer writes this file itself and does not pass those options, so leaving it alone is what
        // keeps its absolute %ProgramData% path intact.
        if (opsConfigured)
        {
            Write(opsPath, ops);
        }

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
    /// Writes where device codes are kept, and says so in the notes.
    /// </summary>
    /// <remarks>
    /// Nothing said means nothing changed, and that distinction is the whole point. The installers pass only what
    /// somebody filled in, and this is also the step used to fix an address or rotate a certificate months later.
    /// Reading "not given" as "clear it" would drop the connection string — and with it every device code — on an
    /// installation that only meant to change something else.
    /// </remarks>
    private static DatabaseChoice? ConfigureDatabase(JsonObject section, ServerSetupOptions options, List<string> notes)
    {
        var provider = Given(options.DatabaseProvider);
        var path = Given(options.DatabasePath);
        var postgres = Given(options.PostgresConnectionString);

        if (provider is null && path is null && postgres is null)
        {
            return null;
        }

        var (chosen, connectionString, databasePath) = Decide(provider, path, postgres, ReadDatabase(section));
        var database = Section(section, DatabaseSectionName);
        database["Provider"] = chosen;

        switch (chosen)
        {
            case ProviderNone:
                database.Remove("ConnectionString");
                database.Remove("Path");
                section.Remove(LegacyPostgresKey);
                notes.Add("Device codes are kept in memory only: a restart gives every device a new code. Give this server a database if machines are meant to be reachable with nobody present.");
                break;

            case ProviderPostgres:
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    // Writing a provider with no string leaves a server that cannot start, and the installer would
                    // report success because restarting a failed unit is not an error it looks at.
                    throw new InvalidOperationException(
                        "PostgreSQL was chosen but no connection string was given. Pass one (for example Host=127.0.0.1;Port=5432;Database=remotedesktop;Username=remotedesktop;Password=...) or choose SQLite.");
                }

                database["ConnectionString"] = connectionString;
                database.Remove("Path");

                // An older server reads this key and nothing else, and it means the same thing, so it is written too.
                section[LegacyPostgresKey] = connectionString;
                notes.Add("Device codes are kept in PostgreSQL.");
                break;

            default:
                database["Path"] = databasePath;
                database.Remove("ConnectionString");
                section.Remove(LegacyPostgresKey);
                notes.Add($"Device codes are kept in SQLite at {databasePath}. Back that file up — losing it gives every device a new code.");
                break;
        }

        return new DatabaseChoice(chosen, connectionString, databasePath);
    }

    /// <summary>
    /// Points the monitoring console at a database. It shares the provider with the signaling server, because an
    /// operator who chose PostgreSQL meant the installation, not one of its processes.
    /// </summary>
    /// <returns><c>true</c> when the console's settings were changed and have to be written.</returns>
    private static bool ConfigureOps(
        JsonObject settings,
        DatabaseChoice? database,
        ServerSetupOptions options,
        List<string> notes)
    {
        var listen = Given(options.OpsListen);
        var databasePath = Given(options.OpsDatabasePath);
        if (database is null && listen is null && databasePath is null)
        {
            return false;
        }

        var section = Section(settings, "Ops");
        if (listen is not null)
        {
            section["Listen"] = listen;
        }

        if (database is not { } choice)
        {
            if (databasePath is null)
            {
                return true;
            }

            // Only a console path was given. That is still a SQLite console: it has no other provider to fall back to.
            var sqlite = Section(section, DatabaseSectionName);
            sqlite["Provider"] = ProviderSqlite;
            sqlite["Path"] = databasePath;
            sqlite.Remove("ConnectionString");
            return true;
        }

        var console = Section(section, DatabaseSectionName);
        if (choice.Provider == ProviderPostgres)
        {
            console["Provider"] = ProviderPostgres;
            console["ConnectionString"] = choice.ConnectionString;
            console.Remove("Path");
            notes.Add("The monitoring console uses the same PostgreSQL database.");
        }
        else
        {
            // "none" is a legitimate choice for the signaling server (memory only) but not for the console: a console
            // with nowhere to write cannot answer a single question, so it always gets a SQLite file.
            var path = databasePath ?? DefaultOpsSqlitePath();
            console["Provider"] = ProviderSqlite;
            console["Path"] = path;
            console.Remove("ConnectionString");
            notes.Add($"The monitoring console keeps its data in SQLite at {path}.");
        }

        return true;
    }

    /// <summary>What the settings file already says about the database, which is what <c>auto</c> decides from.</summary>
    private static ExistingDatabase ReadDatabase(JsonObject section)
    {
        var database = section[DatabaseSectionName] as JsonObject;
        return new ExistingDatabase(
            Text(database, "Provider"),
            Text(database, "ConnectionString"),
            Text(database, "Path"),
            Text(section, LegacyPostgresKey));
    }

    private static (string Provider, string? ConnectionString, string? Path) Decide(
        string? provider,
        string? path,
        string? postgres,
        ExistingDatabase existing)
    {
        if (provider is not null)
        {
            if (IsAuto(provider))
            {
                // What the caller gave this run wins over what the machine had before: an installer that filled the
                // box in said what it wanted, and "auto" only means "and otherwise decide for this machine".
                if (postgres is { Length: > 0 } && !IsAuto(postgres))
                {
                    return (ProviderPostgres, postgres, null);
                }

                return path is { Length: > 0 }
                    ? (ProviderSqlite, null, path)
                    : Automatic(existing);
            }

            if (string.Equals(provider, ProviderNone, StringComparison.OrdinalIgnoreCase))
            {
                return (ProviderNone, null, null);
            }

            if (string.Equals(provider, ProviderPostgres, StringComparison.OrdinalIgnoreCase))
            {
                return (ProviderPostgres, FirstGiven(postgres, existing.ConnectionString, existing.LegacyConnectionString), null);
            }

            if (string.Equals(provider, ProviderSqlite, StringComparison.OrdinalIgnoreCase))
            {
                return (ProviderSqlite, null, FirstGiven(path, existing.Path) ?? DefaultSqlitePath());
            }

            throw new InvalidOperationException($"The database provider must be auto, none, postgres or sqlite (got '{provider}').");
        }

        if (postgres is not null)
        {
            // An empty connection string is how the installers have always said "no database"; "auto" is the value
            // the package ships when nobody filled the box in, and means "decide for this machine".
            return postgres.Length == 0
                ? (ProviderNone, null, null)
                : IsAuto(postgres) ? Automatic(existing) : (ProviderPostgres, postgres, null);
        }

        // A file with no provider can only mean the database that has one.
        return (ProviderSqlite, null, path!);
    }

    /// <summary>
    /// What <c>auto</c> resolves to on this machine: keep what is already configured, and otherwise start a SQLite
    /// database. An existing PostgreSQL connection string wins because it is the one case where switching would
    /// reissue every device code on the installation.
    /// </summary>
    private static (string Provider, string? ConnectionString, string? Path) Automatic(ExistingDatabase existing)
    {
        if (string.Equals(existing.Provider, ProviderSqlite, StringComparison.OrdinalIgnoreCase))
        {
            return (ProviderSqlite, null, existing.Path ?? DefaultSqlitePath());
        }

        var connectionString = FirstGiven(existing.ConnectionString, existing.LegacyConnectionString);
        if (string.Equals(existing.Provider, ProviderPostgres, StringComparison.OrdinalIgnoreCase))
        {
            return connectionString is null
                ? throw new InvalidOperationException("The database provider is postgres but there is no connection string to use. Pass one, or pass 'none' to keep device codes in memory.")
                : (ProviderPostgres, connectionString, null);
        }

        return connectionString is not null
            ? (ProviderPostgres, connectionString, null)
            : (ProviderSqlite, null, existing.Path ?? DefaultSqlitePath());
    }

    /// <summary>
    /// Where a SQLite database goes when the installer did not name a file: the platform's own place for a service's
    /// data, not the install directory. An upgrade replaces what is in the install directory, and this file is the
    /// only thing standing between an unattended machine and a new device code.
    /// </summary>
    private static string DefaultSqlitePath() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Remote Desktop Server", "rendezvous.db")
            : OperatingSystem.IsMacOS()
                ? "/usr/local/remotedesktop-server/var/rendezvous.db"
                : "/var/lib/remotedesktop-server/rendezvous.db";

    /// <summary>Where the console's SQLite file goes when the console was not given one.</summary>
    private static string DefaultOpsSqlitePath() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Remote Desktop Server", "ops.db")
            : OperatingSystem.IsMacOS()
                ? "/usr/local/remotedesktop-server/var/ops.db"
                : "/var/lib/remotedesktop-server/ops.db";

    /// <summary>The provider and the values the provider needs, as they were written.</summary>
    private readonly record struct DatabaseChoice(string Provider, string? ConnectionString, string? Path);

    /// <summary>A value the caller gave, with the difference between "not given" (null) and "given as empty" kept.</summary>
    private static string? Given(string? value) => value?.Trim();

    private static string? FirstGiven(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? Text(JsonObject? settings, string name) =>
        settings?[name]?.GetValue<string>() is { Length: > 0 } value ? value : null;

    /// <summary>
    /// What the settings file said before this run. The provider is null when the file predates it, which is what
    /// tells <see cref="Automatic"/> to look at the connection string instead.
    /// </summary>
    private readonly record struct ExistingDatabase(
        string? Provider,
        string? ConnectionString,
        string? Path,
        string? LegacyConnectionString);

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
            notes.Add("Signaling is served over plain http: anyone on the way can read the device codes and take a relay ticket. Give the installer a certificate (see docs/ops/README.md) or put a reverse proxy in front.");
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

    /// <summary>The extra names a generated certificate has to answer, split on the separators an operator is likely to type.</summary>
    private static string[] AdditionalNames(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Loads the certificate the way Kestrel will, so a wrong password, a missing key or a file that is not a
    /// certificate fails the installation instead of leaving a server that cannot start.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file cannot be used as a server certificate.</exception>
    private static void ValidateCertificateFile(string certificatePath, string? password)
    {
        var secret = IsAuto(password) ? string.Empty : password!;

        try
        {
            if (Path.GetExtension(certificatePath) is ".pfx" or ".p12")
            {
                using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                    certificatePath,
                    secret,
                    X509KeyStorageFlags.EphemeralKeySet);

                if (!certificate.HasPrivateKey)
                {
                    throw new InvalidOperationException($"The certificate '{certificatePath}' has no private key, so the server cannot serve TLS with it.");
                }

                return;
            }

            var keyPath = KeyBeside(certificatePath)
                ?? throw new InvalidOperationException(
                    $"The certificate '{certificatePath}' is not a PKCS#12 file and has no key beside it. A PEM certificate needs its key in a file with the same name and a .key extension ({Path.ChangeExtension(certificatePath, ".key")}).");

            using var pair = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
            if (!pair.HasPrivateKey)
            {
                throw new InvalidOperationException($"The certificate '{certificatePath}' and '{keyPath}' do not form a key pair, so the server cannot serve TLS with them.");
            }
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException($"The certificate '{certificatePath}' could not be read: {ex.Message}", ex);
        }
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

        // These files carry the relay shared key and, when PostgreSQL is used, its password. The service reads them
        // as its own account; nobody else on the machine has any business doing so. The package's post-install step
        // gives the group the right name (the service account); the mode is set here so a manual run cannot leave a
        // world-readable key behind even for the moment before that step.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                temporary,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }

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
