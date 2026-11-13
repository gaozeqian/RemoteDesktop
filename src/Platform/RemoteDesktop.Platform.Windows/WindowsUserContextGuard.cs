using System.Runtime.Versioning;
using System.Security.Principal;
using RemoteDesktop.Abstractions.Platform;

namespace RemoteDesktop.Platform.Windows;

/// <summary>Refuses to serve files or shells while running as LocalSystem, i.e. in the agent that owns the login screen.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsUserContextGuard : IUserContextGuard
{
    private static readonly bool IsSystem = WindowsIdentity.GetCurrent().IsSystem;

    public string? GetRefusalReason() =>
        IsSystem ? "This is not available on the login screen; it can be used once a user is signed in." : null;
}
