using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Platform;

namespace RemoteDesktop.Platform.Windows;

/// <summary>Values the client needs before it builds its service container.</summary>
public static class WindowsPlatform
{
    /// <summary>The same installation value <c>AddWindowsPlatform</c> registers, for the hosting-mode decision.</summary>
    [SupportedOSPlatform("windows")]
    public static IHostInstallation CreateHostInstallation() => new WindowsHostInstallation();
}
