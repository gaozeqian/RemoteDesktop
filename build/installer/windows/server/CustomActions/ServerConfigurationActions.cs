using WixToolset.Dtf.WindowsInstaller;
using System.Diagnostics;
using System.Linq;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace RemoteDesktop.Server.Installer.CustomActions;

/// <summary>
/// The steps that are more than a command line: keeping the live settings outside the MSI's file table, securing the
/// directory that holds them, running the server's own configuration step without putting secrets on a command line,
/// registering the console's local nodes, and removing the data and certificate on request.
/// </summary>
/// <remarks>
/// The server executable's <c>--configure-system</c> remains the one implementation of what a setting means. These
/// actions only decide where the files live, how the child process is started, and what happens on upgrade/uninstall.
/// Splitting it this way means the installer never has to reimplement a rule the server enforces at start-up.
/// </remarks>
public static class ServerConfigurationActions
{
    private const string ConfigFolderProperty = "CONFIGFOLDER";
    private const string InstallFolderProperty = "INSTALLFOLDER";
    private const string LogFileName = "server-setup.log";

    /// <summary>The three programs whose settings move with the service, and their folders.</summary>
    private static readonly string[] Roles = ["Rendezvous", "Relay", "Ops"];

    /// <summary>The keys <see cref="PrepareServerData"/> packs for the deferred actions.</summary>
    private static readonly string[] SetupDataKeys =
    [
        "InstallDirectory", "ConfigDirectory", "RendezvousUrl", "RelayEndpoint", "RelaySharedKey",
        "DatabaseProvider", "DatabasePath", "PostgresConnectionString", "TlsCertificatePath", "TlsPassword",
        "TlsHostName",
    ];

    [CustomAction]
    public static ActionResult SecureConfigDirectory(Session session) =>
        Run(session, "SecureConfigDirectory", () =>
        {
            var directory = ConfigDirectory(session);

            // This runs before InstallInitialize, while the migration can still read the old product, so it is an
            // immediate action and is impersonated as the person installing. A per-machine install started from a
            // double-click is not necessarily an elevated client: msiexec can run the UI and the immediate actions with
            // the filtered token and do the privileged work in the service. Restricting the directory to
            // SYSTEM/Administrators here would therefore take away the very access this process needs for the
            // enumeration below, and the install would fail after creating an empty directory. Keep the installing
            // user for the duration of the install; SecureConfigDirectoryFinal, a deferred action running as SYSTEM,
            // applies the final ACL once the files are in place.
            try
            {
                Directory.CreateDirectory(directory);
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    new DirectoryInfo(directory).SetAccessControl(TemporaryDirectorySecurity(identity.User));
                }

                session.Log($"Config directory {directory} prepared for the installation; the deferred step restricts it to SYSTEM and Administrators.");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or PrivilegeNotHeldException or IOException)
            {
                // An existing directory secured by an earlier install may not be writable by this user; the deferred
                // action runs as SYSTEM and can secure it. Do not turn "this token cannot change the ACL yet" into a
                // failed installation.
                session.Log($"Config directory {directory} could not be prepared by this user; the deferred step will secure it: {ex.Message}");
            }

            return ActionResult.Success;
        });

    /// <summary>
    /// Applies the final, SYSTEM/Administrators-only ACL once the files are in place.
    /// </summary>
    /// <remarks>
    /// This is the deferred half of <see cref="SecureConfigDirectory"/>. It runs as SYSTEM, so it can tighten a
    /// directory the installing user was deliberately allowed to write to during the immediate phase, and it can read
    /// files that an earlier installation already restricted. The root ACL is set last so every child can still be
    /// enumerated while its own ACL is being changed.
    /// </remarks>
    [CustomAction]
    public static ActionResult SecureConfigDirectoryFinal(Session session) =>
        Run(session, "SecureConfigDirectoryFinal", () =>
        {
            var data = ReadServerData(session);
            var directory = ConfigDirectory(data);
            Directory.CreateDirectory(directory);

            // Materialize the list before touching any ACL, so tightening one directory cannot hide the entries below
            // it from the enumeration that is still filling this list.
            var entries = Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).ToList();
            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    new DirectoryInfo(entry).SetAccessControl(RestrictedDirectorySecurity());
                }
                else
                {
                    new FileInfo(entry).SetAccessControl(RestrictedFileSecurity());
                }
            }

            new DirectoryInfo(directory).SetAccessControl(RestrictedDirectorySecurity());
            session.Log($"Config directory {directory} restricted to SYSTEM and Administrators.");
            return ActionResult.Success;
        });

    /// <summary>
    /// Copies the live settings an older package kept under Program Files into ProgramData before the old product is
    /// removed. Runs only during a major upgrade, and only when the destination is not there yet, so a second upgrade
    /// does not overwrite what an operator changed in the meantime.
    /// </summary>
    [CustomAction]
    public static ActionResult MigrateLiveSettings(Session session) =>
        Run(session, "MigrateLiveSettings", () =>
        {
            var install = InstallDirectory(session);
            var config = ConfigDirectory(session);
            Directory.CreateDirectory(config);

            // The migration exists for the first upgrade from a package that kept the settings under Program Files.
            // Once the live directory has anything in it, an earlier upgrade has already migrated (or an operator has
            // configured the server), and running again would overwrite a newer file with an old one. This check also
            // turns "the existing directory is restricted and this user cannot read it" into a skip rather than a
            // failed install; the deferred SecureConfigDirectoryFinal can still secure it as SYSTEM.
            if (HasEntries(config))
            {
                session.Log($"Settings migration skipped: {config} already contains live settings.");
                return ActionResult.Success;
            }

            foreach (var role in Roles)
            {
                var source = Path.Combine(install, role, "appsettings.json");
                var destinationDirectory = Path.Combine(config, role);
                var destination = Path.Combine(destinationDirectory, "appsettings.json");
                if (!File.Exists(source) || File.Exists(destination))
                {
                    session.Log($"Settings migration skipped for {role}: source '{source}' exists={File.Exists(source)}; " +
                                $"destination '{destination}' exists={File.Exists(destination)}.");
                    continue;
                }

                Directory.CreateDirectory(destinationDirectory);
                File.Copy(source, destination);
                session.Log($"Migrated {source} to {destination}.");
            }

            return ActionResult.Success;
        });

    /// <summary>Fills in the machine name when the page/command line did not name a TLS host.</summary>
    [CustomAction]
    public static ActionResult SetDefaultTlsHostName(Session session) => Run(session, "SetDefaultTlsHostName", () =>
    {
        if (string.IsNullOrWhiteSpace(session["TLSCERTIFICATEHOSTNAME"]))
        {
            session["TLSCERTIFICATEHOSTNAME"] = Environment.MachineName;
        }

        return ActionResult.Success;
    });

    /// <summary>
    /// Packs the install-time properties into the custom-action data that the deferred actions read.
    /// </summary>
    /// <remarks>
    /// Windows Installer gives a deferred managed action only its CustomActionData; reading an ordinary session
    /// property from it throws (DTF: "Cannot access session details from a non-immediate custom action"). The values
    /// include the relay key, a PostgreSQL connection string and the TLS password, so each value is base64-encoded
    /// before it is put into the name=value list: DTF separates the pairs with semicolons, and a PostgreSQL connection
    /// string contains them. Every action that receives the data is marked HideTarget, which keeps it out of the MSI
    /// log, the same way the fixed-password file's path is kept out.
    /// </remarks>
    [CustomAction]
    public static ActionResult PrepareServerData(Session session) => Run(session, "PrepareServerData", () =>
    {
        var values = new Dictionary<string, string>
        {
            ["InstallDirectory"] = session["SERVERINSTALLDIR"] ?? string.Empty,
            ["ConfigDirectory"] = session["SERVERCONFIGDIR"] ?? string.Empty,
            ["RendezvousUrl"] = session["RENDEZVOUSURL"] ?? string.Empty,
            ["RelayEndpoint"] = session["RELAYPUBLICENDPOINT"] ?? string.Empty,
            ["RelaySharedKey"] = session["RELAYSHAREDKEY"] ?? string.Empty,
            ["DatabaseProvider"] = session["DATABASEPROVIDER"] ?? string.Empty,
            ["DatabasePath"] = session["DATABASEPATH"] ?? string.Empty,
            ["PostgresConnectionString"] = session["POSTGRESCONNECTIONSTRING"] ?? string.Empty,
            ["TlsCertificatePath"] = session["TLSCERTIFICATEPATH"] ?? string.Empty,
            ["TlsPassword"] = session["TLSCERTIFICATEPASSWORD"] ?? string.Empty,
            ["TlsHostName"] = session["TLSCERTIFICATEHOSTNAME"] ?? string.Empty,
        };

        var data = new StringBuilder();
        foreach (var pair in values)
        {
            if (data.Length > 0)
            {
                data.Append(';');
            }

            data.Append(pair.Key).Append('=').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Value)));
        }

        session["SERVERCONFIGDATA"] = data.ToString();
        session.Log("Prepared the deferred server configuration data.");
        return ActionResult.Success;
    });

    /// <summary>
    /// Runs the server's own <c>--configure-system</c> step, then registers the two local nodes in the console.
    /// </summary>
    /// <remarks>
    /// The secrets travel in the child process's environment, never on its command line: a process list is readable by
    /// every local user, and the MSI log records the target of a command-line custom action. The server reads the same
    /// three environment variables when the command-line option is absent.
    /// </remarks>
    [CustomAction]
    public static ActionResult ConfigureServer(Session session) =>
        Run(session, "ConfigureServer", () =>
        {
            var data = ReadServerData(session);
            var install = InstallDirectory(data);
            var config = ConfigDirectory(data);
            var rendezvousUrl = Value(data, "RendezvousUrl", "http://0.0.0.0:5080");
            var relayEndpoint = Value(data, "RelayEndpoint", "auto");
            var relayKey = Value(data, "RelaySharedKey", "auto");
            var databaseProvider = Value(data, "DatabaseProvider", "auto");
            var databasePath = Value(data, "DatabasePath", string.Empty);
            var postgres = Value(data, "PostgresConnectionString", "auto");
            var tlsPath = Value(data, "TlsCertificatePath", "auto");
            var tlsPassword = Value(data, "TlsPassword", string.Empty);
            var tlsHostName = Value(data, "TlsHostName", Environment.MachineName);

            var arguments = new List<string>
            {
                "--configure-system",
                "--install-directory", install,
                "--config-directory", config,
                "--urls", rendezvousUrl,
                "--relay-endpoint", relayEndpoint,
                "--database", databaseProvider,
                "--database-path", databasePath,
                "--metrics-enabled",
                "--tls-hostname", tlsHostName,
            };

            var tlsEnabled = !IsAuto(tlsPath);
            if (tlsEnabled)
            {
                arguments.Add("--tls-certificate");
                arguments.Add(tlsPath);

                // Only a certificate this step generates has a known public half in tls\signaling.cer. A .pfx the
                // operator supplied may be password-protected, and the console cannot load it as a pin; those
                // deployments rely on the system trust store, as the macOS package explains.
                if (string.Equals(tlsPath, "generate", StringComparison.OrdinalIgnoreCase))
                {
                    arguments.Add("--ops-node-certificate-path");
                    arguments.Add(Path.Combine(config, "tls", "signaling.cer"));
                }
            }

            var environment = new Dictionary<string, string>
            {
                ["RD_RELAY_KEY"] = relayKey,
                ["RD_POSTGRES"] = postgres,
                ["RD_TLS_PASSWORD"] = tlsPassword,
            };

            var executable = Path.Combine(install, "Rendezvous", "RemoteDesktop.Server.Rendezvous.exe");
            var result = RunProcess(session, executable, install, arguments, environment, config);
            if (result.ExitCode != 0)
            {
                return ActionResult.Failure;
            }

            SeedLocalNodes(session, install, config, rendezvousUrl, tlsEnabled);
            return ActionResult.Success;
        });

    /// <summary>
    /// An upgrade keeps the console's node table; only the two rows the installer created need a fresh base URL when
    /// the installation's TLS state (and therefore the scheme) changed.
    /// </summary>
    [CustomAction]
    public static ActionResult SyncLocalNodes(Session session) =>
        Run(session, "SyncLocalNodes", () =>
        {
            var data = ReadServerData(session);
            var install = InstallDirectory(data);
            var config = ConfigDirectory(data);
            var settings = Path.Combine(config, "Rendezvous", "appsettings.json");
            if (!File.Exists(settings))
            {
                return ActionResult.Success;
            }

            // An upgrade preserves the live files, so ConfigureServer does not run. The relay key and the console's
            // local nodes both have to be re-synchronized here; --configure-system with no options leaves the
            // address, TLS, database and metrics alone and only rewrites the two shared-key entries.
            SyncRelayKey(session, install, config);

            var urls = ReadUrls(settings);
            if (urls is null)
            {
                session.Log($"Could not read Urls from {settings}; the console's local nodes were left alone.");
                return ActionResult.Success;
            }

            SeedLocalNodes(session, install, config, urls, urls.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
            return ActionResult.Success;
        });

    /// <summary>Removes the settings, data and generated certificate when the operator asked for it.</summary>
    [CustomAction]
    public static ActionResult CleanupServer(Session session) =>
        Run(session, "CleanupServer", () =>
        {
            var data = ReadServerData(session);
            var config = ConfigDirectory(data);
            if (Directory.Exists(config))
            {
                Directory.Delete(config, recursive: true);
                session.Log($"Removed {config}.");
            }

            // The generated certificate lives in the machine store, not in the config directory. Only the one this
            // product created is removed; an operator-supplied .pfx was never imported by us.
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            var subject = $"CN=RemoteDesktop Server ({Environment.MachineName})";
            foreach (var certificate in store.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, subject, validOnly: false))
            {
                store.Remove(certificate);
                session.Log($"Removed the generated certificate {subject} from LocalMachine\\My.");
            }

            return ActionResult.Success;
        });

    /// <summary>
    /// Rewrites the relay shared key on an upgrade, without touching any of the values an operator may have changed.
    /// </summary>
    /// <remarks>
    /// <c>--configure-system</c> with no address, database, TLS or metrics options leaves those sections as they are,
    /// but still reads the existing key and writes it into both <c>Rendezvous:RelaySharedKey</c> and
    /// <c>Relay:SharedKey</c>. A fresh install already does this in <see cref="ConfigureServer"/>; an upgrade skips
    /// that action, and before this the relay kept an empty <c>SharedKey</c> while the signaling server had the real
    /// one, so every relayed ticket was rejected.
    /// </remarks>
    private static void SyncRelayKey(Session session, string install, string config)
    {
        var executable = Path.Combine(install, "Rendezvous", "RemoteDesktop.Server.Rendezvous.exe");
        if (!File.Exists(executable))
        {
            session.Log($"The relay shared key was not synchronized: {executable} is missing.");
            return;
        }

        var arguments = new List<string>
        {
            "--configure-system",
            "--install-directory", install,
            "--config-directory", config,
        };

        var result = RunProcess(session, executable, install, arguments, new Dictionary<string, string>(), config);
        if (result.ExitCode != 0)
        {
            session.Log($"The relay shared key could not be synchronized (exit {result.ExitCode}); run Set-ServerConfiguration.");
        }
    }

    private static void SeedLocalNodes(
        Session session,
        string install,
        string config,
        string rendezvousUrl,
        bool tlsEnabled)
    {
        var ops = Path.Combine(install, "Ops", "RemoteDesktop.Server.Ops.exe");
        if (!File.Exists(ops))
        {
            return;
        }

        var port = 5080;
        if (Uri.TryCreate(rendezvousUrl, UriKind.Absolute, out var parsed) && parsed.Port > 0)
        {
            port = parsed.Port;
        }

        // The console is local, and NodeTokenGuard's empty-token mode authorizes only loopback. The TLS certificate is
        // pinned by the console (NodeCertificatePin compares the certificate itself and ignores the host name), so
        // 127.0.0.1 works even with a certificate issued for the machine name. Using the TLS host name here made every
        // /metrics scrape and the device page answer 401.
        const string host = "127.0.0.1";
        var scheme = tlsEnabled ? "https" : "http";
        var nodeUrl = $"{scheme}://{host}:{port}";
        var opsConfig = Path.Combine(config, "Ops");

        var arguments = new List<string>
        {
            "--seed-local-nodes",
            "--config-directory", opsConfig,
            "--rendezvous-base-url", nodeUrl,
            "--relay-base-url", "http://127.0.0.1:5083",
        };

        var seeded = RunProcess(session, ops, install, arguments, new Dictionary<string, string>(), config);
        if (seeded.ExitCode != 0)
        {
            session.Log($"The local nodes could not be registered (exit {seeded.ExitCode}); add them on the console's node page.");
            return;
        }

        arguments[0] = "--sync-local-nodes";
        var synced = RunProcess(session, ops, install, arguments, new Dictionary<string, string>(), config);
        if (synced.ExitCode != 0)
        {
            session.Log($"The local node addresses could not be updated (exit {synced.ExitCode}); check the console's node page.");
        }
    }

    private static string? ReadUrls(string path)
    {
        try
        {
            var match = Regex.Match(File.ReadAllText(path), "\"Urls\"\\s*:\\s*\"(?<urls>[^\"]+)\"");
            return match.Success ? match.Groups["urls"].Value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ProcessResult RunProcess(
        Session session,
        string executable,
        string workingDirectory,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string configDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = string.Join(" ", arguments.Select(Quote)),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var pair in environment)
        {
            startInfo.EnvironmentVariables[pair.Key] = pair.Value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {executable}.");

        // Read both pipes before waiting: a child that writes more than a pipe buffer would otherwise block while
        // this thread waits for it to exit.
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AppendLog(session, configDirectory, output, error);
        return new ProcessResult(process.ExitCode, output, error);
    }

    private static void AppendLog(Session session, string configDirectory, string output, string error)
    {
        foreach (var line in (output + error).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            session.Log(line);
        }

        try
        {
            var path = Path.Combine(configDirectory, LogFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, output + error, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            session.Log($"The setup output could not be written to {LogFileName}: {ex.Message}");
        }
    }

    /// <summary>Quotes one argument for <see cref="ProcessStartInfo.Arguments"/>.</summary>
    /// <remarks>
    /// The install and configuration directories come from MSI directory properties with a trailing backslash. A
    /// plain <c>"...\"</c> lets that backslash escape the closing quote, so the child sees one long argument
    /// containing the rest of the command line (the first real install failed with "The filename, directory name, or
    /// volume label syntax is incorrect" for exactly that reason). Doubling the run of backslashes before the closing
    /// quote is the standard CommandLineToArgvW rule.
    /// </remarks>
    private static string Quote(string value)
    {
        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                quoted.Append(character);
            }
            else if (character == '"')
            {
                quoted.Append('\\', backslashes + 1).Append('"');
                backslashes = 0;
            }
            else
            {
                quoted.Append(character);
                backslashes = 0;
            }
        }

        quoted.Append('\\', backslashes).Append('"');
        return quoted.ToString();
    }

    private static string InstallDirectory(Session session) =>
        session["SERVERINSTALLDIR"] is { } value && !string.IsNullOrWhiteSpace(value)
            ? TrimDirectory(value)
            : throw new InvalidOperationException("SERVERINSTALLDIR is not set.");

    private static string ConfigDirectory(Session session) =>
        session["SERVERCONFIGDIR"] is { } value && !string.IsNullOrWhiteSpace(value)
            ? TrimDirectory(value)
            : DefaultConfigDirectory();

    private static string InstallDirectory(IReadOnlyDictionary<string, string> data) =>
        data.TryGetValue("InstallDirectory", out var value) && !string.IsNullOrWhiteSpace(value)
            ? TrimDirectory(value)
            : throw new InvalidOperationException("SERVERINSTALLDIR is not set.");

    private static string ConfigDirectory(IReadOnlyDictionary<string, string> data) =>
        data.TryGetValue("ConfigDirectory", out var value) && !string.IsNullOrWhiteSpace(value)
            ? TrimDirectory(value)
            : DefaultConfigDirectory();

    private static string DefaultConfigDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Remote Desktop Server");

    /// <summary>Drops the trailing separator directory properties carry, keeping a bare root such as <c>C:\</c>.</summary>
    private static string TrimDirectory(string value)
    {
        var root = Path.GetPathRoot(value);
        return value.Length > (root?.Length ?? 0)
            ? value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : value;
    }

    private static string Value(IReadOnlyDictionary<string, string> data, string key, string fallback) =>
        data.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;

    /// <summary>Unpacks what <see cref="PrepareServerData"/> put into the deferred action's CustomActionData.</summary>
    private static Dictionary<string, string> ReadServerData(Session session)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in SetupDataKeys)
        {
            if (session.CustomActionData.TryGetValue(key, out var encoded) && !string.IsNullOrEmpty(encoded))
            {
                values[key] = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
        }

        return values;
    }

    private static bool IsAuto(string value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the directory has anything below it; unreadable counts as "yes, leave it alone".</summary>
    private static bool HasEntries(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return true;
        }
    }

    private static DirectorySecurity RestrictedDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sidType in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sidType, domainSid: null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>The final ACL plus the installing user, so the immediate phase and the migration can still write.</summary>
    private static DirectorySecurity TemporaryDirectorySecurity(SecurityIdentifier? currentUser)
    {
        var security = RestrictedDirectorySecurity();
        if (currentUser is not null)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }

    private static FileSecurity RestrictedFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sidType in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sidType, domainSid: null),
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        return security;
    }

    private static ActionResult Run(Session session, string name, Func<ActionResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            session.Log($"{name} failed: {ex}");
            return ActionResult.Failure;
        }
    }

    private readonly struct ProcessResult
    {
        public ProcessResult(int exitCode, string output, string error)
        {
            ExitCode = exitCode;
            Output = output;
            Error = error;
        }

        public int ExitCode { get; }

        public string Output { get; }

        public string Error { get; }
    }
}
