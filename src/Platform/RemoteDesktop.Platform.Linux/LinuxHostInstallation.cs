using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Platform;

namespace RemoteDesktop.Platform.Linux;

/// <summary>
/// The Linux controlled end is the systemd user unit the package installs. A socket without the unit is a process
/// whose package has been removed, not a service the user can still see or stop from the package manager.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxHostInstallation : IHostInstallation
{
    /// <summary>The unit the host package installs and the client checks for.</summary>
    internal const string ServiceUnit = "/usr/lib/systemd/user/app-RemoteDesktopHost.service";

    public bool IsInstalled => File.Exists(ServiceUnit);

    public bool StartsWithUserSession => true;

    public string? ServiceName => null;
}
