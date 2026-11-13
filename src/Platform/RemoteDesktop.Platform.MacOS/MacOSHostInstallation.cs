using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Platform;

namespace RemoteDesktop.Platform.MacOS;

/// <summary>
/// The macOS controlled end is the helper application and its login item. The host lives under Library/Application
/// Support rather than /Applications — it has no window, and an app-looking bundle beside Remote Desktop.app invited
/// people to double-click it and wonder why nothing happened. The legacy path is still checked so a client running
/// against an older host package finds its service.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacOSHostInstallation : IHostInstallation
{
    internal const string HostApp = "/Library/Application Support/RemoteDesktop/Host/Remote Desktop Host.app";
    internal const string LegacyHostApp = "/Applications/Remote Desktop Host.app";
    internal const string HostAgentPlist = "/Library/LaunchAgents/com.remotedesktop.host.plist";

    public bool IsInstalled => File.Exists(HostApp) || File.Exists(LegacyHostApp) || File.Exists(HostAgentPlist);

    public bool StartsWithUserSession => true;

    public string? ServiceName => null;
}
