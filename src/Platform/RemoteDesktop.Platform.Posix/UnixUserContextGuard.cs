using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Platform.Posix.Interop;

namespace RemoteDesktop.Platform.Posix;

/// <summary>
/// Refuses to serve files or shells while running as root. The shape of the hazard is the Windows one — a process
/// running as the machine rather than as a person would hand the whole computer to whoever was allowed to connect —
/// even though the way a host gets there is different.
/// </summary>
/// <remarks>
/// On Windows the trigger is a service in Session 0 that owns the login screen. Here there is no equivalent session,
/// so the only way to arrive at root is to have been started as root, which the packaging never does: the controlled
/// end runs under the signed-in account — a <c>systemd --user</c> service on Linux, a launchd agent inside the user's
/// session on macOS. The guard is therefore expected to answer "allowed" in practice, and exists so that a host
/// started the wrong way (a hand-started <c>sudo</c> run, say) refuses the dangerous features instead of quietly
/// offering them.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class UnixUserContextGuard : IUserContextGuard
{
    private static readonly bool IsRoot = LibC.GetEffectiveUserId() == 0;

    public string? GetRefusalReason() =>
        IsRoot ? "This is not available while running as root; the host must run as the signed-in user." : null;
}
