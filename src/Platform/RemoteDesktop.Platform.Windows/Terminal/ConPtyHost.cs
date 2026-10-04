using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Terminal;

namespace RemoteDesktop.Platform.Windows.Terminal;

/// <summary>
/// Runs shells in a Windows pseudoconsole (ConPTY). The shells run as the process that hosts this class, so it belongs
/// in the session agent (the signed-in user), never in a service or the login-screen agent.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConPtyHost : IPtyHost
{
    // The names the controller sees, best default first. They are looked up here; a controller never supplies a path.
    private static readonly string[] ShellNames = ["pwsh", "powershell", "cmd"];

    public IReadOnlyList<string> GetAvailableShells() => [.. ShellNames.Where(name => ResolveShell(name) is not null)];

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

        var arguments = name is "cmd" ? options.Arguments : ["-NoLogo", .. options.Arguments];
        var commandLine = string.Join(' ', new[] { Quote(path) }.Concat(arguments.Select(Quote)));
        var directory = options.WorkingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return ConPtySession.Start(commandLine, directory, options.Columns, options.Rows);
    }

    private static string? ResolveShell(string name)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        switch (name)
        {
            case "cmd":
                var comSpec = Environment.GetEnvironmentVariable("ComSpec");
                return comSpec is not null && File.Exists(comSpec) ? comSpec : Existing(Path.Combine(system, "cmd.exe"));
            case "powershell":
                return Existing(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"));
            case "pwsh":
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                return Existing(Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe")) ?? FindOnPath("pwsh.exe");
            default:
                return null;
        }
    }

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    private static string? FindOnPath(string file) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), file))
            .FirstOrDefault(File.Exists);

    private static string Quote(string argument) =>
        argument.Length > 0 && !argument.Any(c => c is ' ' or '\t' or '"') ? argument : $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
