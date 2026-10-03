namespace RemoteDesktop.Abstractions.Platform;

public enum DesktopKind
{
    /// <summary>The interactive desktop of the logged-in user.</summary>
    User,

    /// <summary>Login screen or UAC secure desktop.</summary>
    Secure,
}

/// <summary>
/// Starts the session agent inside the interactive session from the privileged service, because the service itself
/// (Session 0 on Windows, a daemon elsewhere) cannot capture the screen or inject input.
/// </summary>
public interface ISessionLauncher
{
    event EventHandler<DesktopKind>? ActiveDesktopChanged;

    DesktopKind ActiveDesktop { get; }

    Task<IAgentProcess> LaunchAgentAsync(DesktopKind desktop, CancellationToken cancellationToken);
}

public interface IAgentProcess : IAsyncDisposable
{
    int ProcessId { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);
}
