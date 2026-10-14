using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Terminal;

namespace RemoteDesktop.Platform.Posix.Terminal;

/// <summary>
/// Runs shells on a pseudo-terminal. The shells run as whoever this process is, so this belongs wherever the
/// signed-in user's work happens, never in a process running as the machine.
/// </summary>
/// <remarks>
/// <para>
/// What is offered is worked out by looking, not by assuming: a distribution that ships no zsh should not offer one,
/// and a machine whose user has chosen fish should offer that first. On Windows the list is short enough to write
/// down; here it depends on the installation and on what the account was given.
/// </para>
/// <para>
/// The controller picks a name from the list and never supplies a path — the same rule the Windows host follows, and
/// the reason a path in <see cref="PtyStartOptions.Shell"/> is rejected rather than executed.
/// </para>
/// <para>
/// The shell is started as a login shell (<c>-l</c>) so it picks up the user's real environment. Without it a remote
/// terminal would come up with the bare PATH a GUI process inherits — which on Apple Silicon means no Homebrew, and
/// on a desktop session a visibly emptier shell than the one the user gets in their own terminal.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class PosixPtyHost : IPtyHost
{
    // The names a controller may ask for, best default first. Darwin leads with zsh because that is the system's
    // own default shell; Linux leads with bash for the same reason. The login shell, when it resolves, still comes
    // before either.
    private static readonly string[] ShellNames = OperatingSystem.IsMacOS()
        ? ["zsh", "bash", "fish", "sh"]
        : ["bash", "zsh", "fish", "sh"];

    // Where a shell may live. The Homebrew prefixes come first on macOS so an installed newer bash or fish wins over
    // the system one, the way the user's own PATH would have it — /opt/homebrew is Apple Silicon's, /usr/local is
    // Intel's.
    private static readonly string[] SearchDirectories = OperatingSystem.IsMacOS()
        ? ["/opt/homebrew/bin", "/usr/local/bin", "/bin", "/usr/bin", "/sbin", "/usr/sbin"]
        : ["/bin", "/usr/bin", "/usr/local/bin", "/sbin", "/usr/sbin"];

    public IReadOnlyList<string> GetAvailableShells()
    {
        var available = new List<string>();

        // The account's own shell comes first: it is the one the user chose, and the one they get in a terminal on
        // the machine itself. Everything else follows in a fixed order so that the list is stable between calls.
        if (LoginShellName() is { } login && !available.Contains(login) && ResolveShell(login) is not null)
        {
            available.Add(login);
        }

        foreach (var name in ShellNames)
        {
            if (!available.Contains(name) && ResolveShell(name) is not null)
            {
                available.Add(name);
            }
        }

        return available;
    }

    public IPtySession Start(PtyStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var available = GetAvailableShells();
        var name = string.IsNullOrEmpty(options.Shell) ? (available.Count > 0 ? available[0] : null) : options.Shell;
        var path = name is null ? null : ResolveShell(name);
        if (name is null || path is null)
        {
            throw new InvalidOperationException("No such shell is available.");
        }

        var directory = options.WorkingDirectory
            ?? Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // A login shell, so that it reads the same profile files the user's own terminal does and arrives with the
        // PATH and aliases they expect. Without it a remote shell is a visibly different, emptier shell.
        string[] arguments = ["-l", .. options.Arguments];
        return PosixPtySession.Start(path, arguments, directory, options.Columns, options.Rows);
    }

    /// <summary>The shell the account was given, taken from the environment rather than guessed at.</summary>
    private static string? LoginShellName()
    {
        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrEmpty(shell))
        {
            return null;
        }

        var name = Path.GetFileName(shell.TrimEnd('/'));
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>Where a name is on this machine, or <c>null</c> when it is not.</summary>
    private static string? ResolveShell(string name)
    {
        // A path is not a name. Accepting one would turn the shell picker into a way to run anything at all, and the
        // controller is the one supplying it.
        if (name.Contains('/', StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var directory in SearchDirectories)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
