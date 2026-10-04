using System.Runtime.Versioning;
using System.Security.Principal;
using RemoteDesktop.Abstractions.Platform;

namespace RemoteDesktop.Platform.Windows;

/// <summary>Refuses to serve files while running as LocalSystem, i.e. in the agent that owns the login screen.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileAccessGuard : IFileAccessGuard
{
    private static readonly bool IsSystem = WindowsIdentity.GetCurrent().IsSystem;

    public string? GetRefusalReason() =>
        IsSystem ? "Files are not available on the login screen; they can be reached once a user is signed in." : null;
}
