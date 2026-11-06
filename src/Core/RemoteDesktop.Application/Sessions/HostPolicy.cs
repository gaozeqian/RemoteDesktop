using RemoteDesktop.Domain;

namespace RemoteDesktop.Application.Sessions;

/// <summary>
/// The choices the person at this computer has made about what controllers may do.
/// </summary>
/// <remarks>
/// Every capability, the camera included, is a standard remote feature and allowed by default. There is deliberately
/// no machine-wide camera switch: the person here stops a camera that is in use from that session's row in the
/// client's list of incoming sessions, which leaves the rest of the session running.
/// </remarks>
public static class HostPolicy
{
    /// <summary>What a fresh installation allows: all remote capabilities including the camera.</summary>
    public static Permissions Default => Permissions.All;
}
