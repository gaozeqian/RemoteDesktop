using RemoteDesktop.Domain;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Application.Features;

public enum SessionRole
{
    /// <summary>This device controls (or watches) the remote device.</summary>
    Controller,

    /// <summary>This device is being controlled.</summary>
    Host,
}

public sealed class SessionContext(SessionId id, SessionRole role, IPeerConnection connection, AccessGrant grant)
{
    public SessionId Id { get; } = id;

    public SessionRole Role { get; } = role;

    public IPeerConnection Connection { get; } = connection;

    /// <summary>Current grant; replaced when the host downgrades the session.</summary>
    public AccessGrant Grant { get; set; } = grant;
}
