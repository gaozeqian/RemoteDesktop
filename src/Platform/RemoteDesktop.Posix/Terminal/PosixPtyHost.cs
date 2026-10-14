using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Terminal;

namespace RemoteDesktop.Platform.Posix.Terminal;

/// <summary>
/// Runs shells on a pseudo-terminal. The shells run as whoever this process is, so this belongs wherever the signed-in
/// user's work happens, never in a process running as the machine.
/// </summary>
/// <remarks>
/// What is offered is worked out by looking, not by assuming: a distribution that ships no zsh should not offer one,
/// and a machine whose user has chosen fish should offer that first. On Windows the list is short enough to write
/// down; here it depends on the installation and on what the account was given.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class PosixPtyHost : IPtyHost
{
    // The names a controller may ask for. It never supplies a path — that would be a way to run anything at all.
    private static readonly string[] ShellNames = ["bash", "zsh", "fish", "sh"];

    private static readonly string[] SearchDirectories = ["/bin", "/usr/bin", "/usr/local/bin", "/sbin", "/usr/sbin"];

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

        // A login shell, so that it reads the same profile files the user's own terminal does and arrives with the
        // PATH and aliases they expect. Without it a remote shell is a visibly different, emptier shell.
        var arguments = new List<string> { "-l" };
        arguments.AddRange(options.Arguments);

        var directory = options.WorkingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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
        // A name is one path segment. Anything else — a path, a leading dash, a space — is not a shell this host has
        // heard of, and letting it through would be the same as accepting a path.
        if (string.IsNullOrEmpty(name) || name.Contains('/') || name.StartsWith('-'))
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

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
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
