namespace RemoteDesktop.Abstractions.Terminal;

/// <param name="Shell">Executable to run; <c>null</c> selects the platform default shell.</param>
public sealed record PtyStartOptions(string? Shell, IReadOnlyList<string> Arguments, string? WorkingDirectory, int Columns, int Rows);

public interface IPtyHost
{
    /// <summary>Shells offered to the controller (e.g. PowerShell and cmd on Windows, bash and zsh elsewhere).</summary>
    IReadOnlyList<string> GetAvailableShells();

    IPtySession Start(PtyStartOptions options);
}

/// <summary>A pseudo-terminal running a shell as the interactive (non-elevated) user.</summary>
public interface IPtySession : IAsyncDisposable
{
    /// <summary>Raw VT output from the shell.</summary>
    Stream Output { get; }

    /// <summary>Raw input (keystrokes) to the shell.</summary>
    Stream Input { get; }

    void Resize(int columns, int rows);

    Task<int> WaitForExitAsync(CancellationToken cancellationToken);
}
