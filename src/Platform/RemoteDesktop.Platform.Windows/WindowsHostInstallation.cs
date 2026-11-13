using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Platform;

namespace RemoteDesktop.Platform.Windows;

/// <summary>The Windows controlled end is the service the MSI installs; there is no user-session package to check.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsHostInstallation : IHostInstallation
{
    /// <summary>The name the host installer registers and the client uses to identify the service process.</summary>
    internal const string WindowsServiceName = "RemoteDesktopHost";

    public bool IsInstalled => true;

    public bool StartsWithUserSession => false;

    public string? ServiceName => WindowsServiceName;
}
