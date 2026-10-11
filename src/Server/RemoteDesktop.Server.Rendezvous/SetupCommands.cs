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
            RelaySharedKey = Argument(args, "--relay-key"),
            PostgresConnectionString = Argument(args, "--postgres"),
            TlsCertificatePath = Argument(args, "--tls-certificate"),
            TlsCertificatePassword = Argument(args, "--tls-password"),
        };

        var directory = Argument(args, "--install-directory") ?? defaultInstallDirectory;
        try
        {
            var notes = ServerSetup.Configure(directory, options);
            foreach (var note in notes)
            {
                output.WriteLine(note);
            }

            Write(directory, notes, output);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Somebody running this by hand gets a sentence rather than a stack trace, and the settings are left as
            // they were. The installer ignores the exit code and shows nobody the output, so it is written down.
            output.WriteLine(ex.Message);
            Write(directory, [ex.Message], output);
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
