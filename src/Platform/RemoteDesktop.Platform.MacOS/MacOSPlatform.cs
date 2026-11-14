using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Platform;

namespace RemoteDesktop.Platform.MacOS;

/// <summary>Values the client needs before it builds its service container.</summary>
public static class MacOSPlatform
{
    /// <summary>The same installation value <c>AddMacOSPlatform</c> registers, for the hosting-mode decision.</summary>
    [SupportedOSPlatform("macos")]
    public static IHostInstallation CreateHostInstallation() => new MacOSHostInstallation();
}
