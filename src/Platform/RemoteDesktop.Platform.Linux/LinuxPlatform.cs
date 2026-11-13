using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Platform;

namespace RemoteDesktop.Platform.Linux;

/// <summary>Values the client needs before it builds its service container.</summary>
public static class LinuxPlatform
{
    /// <summary>The same installation value <c>AddLinuxPlatform</c> registers, for the hosting-mode decision.</summary>
    [SupportedOSPlatform("linux")]
    public static IHostInstallation CreateHostInstallation() => new LinuxHostInstallation();
}
