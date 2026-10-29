using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>
/// The installer's hook into this executable: <c>--configure-system</c> writes the settings of both servers and
/// exits instead of starting one. It runs elevated during an installation, so it does only what is listed here and
/// reports failure through the exit code.
/// </summary>
/// <remarks>
/// It is this executable that carries the step and not the relay's, because this one is the entry point the installer
/// already has a path to, and because the settings it writes belong to both: they share the key that signs relay
/// tickets.
/// </remarks>
public static class SetupCommands
{
    public const int Success = 0;
    public const int InvalidArguments = 2;

    /// <summary>Where this step leaves what it did, next to the settings it wrote.</summary>
    public const string LogFileName = "server-setup.log";

    private const string Command = "--configure-system";

    public static bool TryRun(string[] args, string defaultInstallDirectory, TextWriter output, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);

        exitCode = Success;
        if (args.Length == 0 || args[0] != Command)
        {
            return false;
        }

        var options = new ServerSetupOptions
        {
            Urls = Argument(args, "--urls"),
            RelayPublicEndpoint = Argument(args, "--relay-endpoint"),
            // The Windows installer runs this as a child process and puts the secret values in its environment rather
            // than on the command line, where they would be visible in the process list and in the MSI log.
            // The command-line options stay for hand runs and for the Linux/macOS packages.
            RelaySharedKey = Argument(args, "--relay-key") ?? Environment.GetEnvironmentVariable("RD_RELAY_KEY"),
            DatabaseProvider = Argument(args, "--database"),
            DatabasePath = Argument(args, "--database-path"),
            PostgresConnectionString = Argument(args, "--postgres") ?? Environment.GetEnvironmentVariable("RD_POSTGRES"),
            TlsCertificatePath = Argument(args, "--tls-certificate"),
            TlsCertificatePassword = Argument(args, "--tls-password") ?? Environment.GetEnvironmentVariable("RD_TLS_PASSWORD"),
            TlsHostName = Argument(args, "--tls-hostname"),
            OpsDatabasePath = Argument(args, "--ops-database-path"),
            OpsListen = Argument(args, "--ops-listen"),
            OpsEventsEndpoint = Argument(args, "--ops-events-endpoint"),
            OpsIngestKey = Argument(args, "--ops-ingest-key") ?? Environment.GetEnvironmentVariable("RD_OPS_INGEST_KEY"),
            OpsNodeCertificatePath = Argument(args, "--ops-node-certificate-path"),
            GatewayListen = Argument(args, "--gateway-listen"),
            GatewayHostName = Argument(args, "--gateway-hostname"),
            MetricsEnabled = Array.Exists(args, argument => string.Equals(argument, "--metrics-enabled", StringComparison.Ordinal)),
        };

        // The program directory (where the shipped appsettings.json defaults live) and the live settings directory are
        // the same path on Linux and macOS. The Windows package passes --config-directory with ProgramData, because a
        // major upgrade removes the old product's files from Program Files. A manual run that does not pass it keeps
        // the historical behaviour: the settings live in --install-directory.
        var installDirectory = Argument(args, "--install-directory") ?? defaultInstallDirectory;
        var configDirectory = Argument(args, "--config-directory") ?? installDirectory;
        var defaultsDirectory = OperatingSystem.IsWindows()
            && !string.Equals(Path.GetFullPath(configDirectory), Path.GetFullPath(installDirectory), StringComparison.OrdinalIgnoreCase)
                ? installDirectory
                : null;

        try
        {
            var notes = ServerSetup.Configure(configDirectory, options, defaultsDirectory);
            foreach (var note in notes)
            {
                output.WriteLine(note);
            }

            Write(configDirectory, notes, output);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Somebody running this by hand gets a sentence rather than a stack trace, and the settings are left as
            // they were. The installer ignores the exit code and shows nobody the output, so it is written down.
            output.WriteLine(ex.Message);
            Write(configDirectory, [ex.Message], output);
            exitCode = InvalidArguments;
            return true;
        }
    }

    /// <summary>
    /// Leaves the notes in the install directory. The installer runs this through cmd.exe and ignores what it says,
    /// so without this an installation whose settings could not be written looks like one that went fine until a
    /// service does not start.
    /// </summary>

    private static void Write(string installDirectory, IEnumerable<string> lines, TextWriter output)
    {
        try
        {
            Directory.CreateDirectory(installDirectory);
            File.AppendAllLines(
                Path.Combine(installDirectory, LogFileName),
                lines.Select(line => $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {line}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            output.WriteLine($"The step could not be written down: {ex.Message}");
        }
    }

    private static string? Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
