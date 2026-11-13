namespace RemoteDesktop.Abstractions.Platform;

/// <summary>
/// Where the controlled end lives on this machine, as far as the client needs to know before it decides whether to
/// host incoming sessions itself or delegate to an installed background host.
/// </summary>
/// <remarks>
/// The three answers are platform facts, so each platform layer provides them and the client no longer recognises
/// operating systems by name: a new platform registers an implementation instead of adding another branch beside every
/// <c>OperatingSystem.Is*</c> call.
/// </remarks>
public interface IHostInstallation
{
    /// <summary>
    /// Whether the installed background host is present. Windows has no separate package to look for — the control
    /// pipe is the evidence — so it answers <c>true</c> and lets the pipe check decide.
    /// </summary>
    bool IsInstalled { get; }

    /// <summary>
    /// Whether the background host starts with the user's desktop session and may still be starting when the client
    /// opens. Where this is true the client waits for the control pipe; where it is false (a Windows service) a
    /// single availability probe is enough.
    /// </summary>
    bool StartsWithUserSession { get; }

    /// <summary>The service name the Windows host registers with the service control manager; null elsewhere.</summary>
    string? ServiceName { get; }
}
