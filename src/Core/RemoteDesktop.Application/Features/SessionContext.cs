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
    private readonly Lock _grantLock = new();

    public SessionId Id { get; } = id;

    public SessionRole Role { get; } = role;

    public IPeerConnection Connection { get; } = connection;

    /// <summary>Current grant; replaced when the host downgrades the session. A new grant derived from the current one goes through <see cref="UpdateGrant"/>.</summary>
    public AccessGrant Grant { get; set; } = grant;

    /// <summary>
    /// Derives the next grant from the current one as a single step. A downgrade and a revoke can arrive at the same
    /// time (the local user, the policy); done as read-then-assign, the second would overwrite the first.
    /// </summary>
    /// <param name="update">Pure and quick: it runs under a lock and may run once per caller, in turn.</param>
    public void UpdateGrant(Func<AccessGrant, AccessGrant> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_grantLock)
        {
            Grant = update(Grant);
        }
    }
}
