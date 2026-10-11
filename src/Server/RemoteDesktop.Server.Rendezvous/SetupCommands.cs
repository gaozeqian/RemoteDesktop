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

    /// <summary>
    /// Checks a value the settings page asks for, without changing anything:
    /// <c>--check database &lt;connection string&gt;</c> or <c>--check relay &lt;host:port&gt;</c>. The page's test
    /// buttons run it, and it exists because a typo in either setting is otherwise only discovered by a server that
    /// will not start.
    /// </summary>
    private const string CheckCommand = "--check";

    public const int CheckFailed = 3;

    /// <returns><c>true</c> when the command line was this command (then <paramref name="exitCode"/> is the result and no server must start).</returns>
    public static bool TryRun(string[] args, string defaultInstallDirectory, TextWriter output, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);

        exitCode = Success;
        if (args.Length > 0 && args[0] == CheckCommand)
        {
            return TryCheck(args, output, out exitCode);
        }

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
    /// <summary>
    /// <c>--check database &lt;connection string&gt;</c> and <c>--check relay &lt;host:port&gt;</c>: report, change nothing.
    /// </summary>
    private static bool TryCheck(string[] args, TextWriter output, out int exitCode)
    {
        exitCode = InvalidArguments;
        if (args.Length < 3)
        {
            output.WriteLine("Usage: --check database <connection string> | --check relay <host:port> [--dialog]");
            return true;
        }

        var (ok, message) = args[1] switch
        {
            "database" => ServerChecks.Database(args[2]),
            "relay" => ServerChecks.Relay(args[2]),
            _ => (false, $"Unknown check '{args[1]}': use 'database' or 'relay'."),
        };

        output.WriteLine(message);

        // The wizard runs this from a custom action with Return="check", where a failure shows Windows Installer's own
        // sentence and drops whatever this process wrote to its output. Showing the reason here is the difference
        // between "the installer refused" and "the installer refused because the password is wrong".
        if (Array.Exists(args, argument => argument == "--dialog"))
        {
            ShowMessage(ok ? "检查通过" : "检查未通过", message);
        }

        exitCode = ok ? Success : CheckFailed;
        return true;
    }

    /// <summary>A message box without dragging a UI framework into a console program.</summary>
    private static void ShowMessage(string title, string text)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const uint Ok = 0x0;
        const uint Information = 0x40;
        const uint Warning = 0x30;
        _ = MessageBox(0, text, title, Ok | (text.StartsWith("检查通过", StringComparison.Ordinal) ? Information : Warning));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBox(nint window, string text, string caption, uint type);

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
