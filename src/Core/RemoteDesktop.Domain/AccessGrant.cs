namespace RemoteDesktop.Domain;

/// <summary>
/// The permissions in effect for one session. Computed on the host side; the controller's UI is never trusted
/// to enforce restrictions such as watch mode.
/// </summary>
public sealed record AccessGrant
{
    private AccessGrant(SessionMode mode, Permissions permissions)
    {
        Mode = mode;
        Permissions = permissions;
    }

    public SessionMode Mode { get; }

    public Permissions Permissions { get; }

    /// <param name="mode">Mode requested by the controller.</param>
    /// <param name="requested">Permissions requested by the controller.</param>
    /// <param name="allowedByHost">Permissions the host's policy (or the local user) allows.</param>
    public static AccessGrant Create(SessionMode mode, Permissions requested, Permissions allowedByHost)
    {
        var effective = requested & allowedByHost;

        // Watch mode is enforced here regardless of what was requested or allowed.
        if (mode == SessionMode.View)
        {
            effective &= Permissions.ViewOnly;
        }

        // Without screen access, a host-side input-capable session would be meaningless and risky.
        if (!effective.HasFlag(Permissions.ViewScreen))
        {
            effective &= ~(Permissions.ControlInput | Permissions.Clipboard);
        }

        return new AccessGrant(mode, effective);
    }

    public bool Allows(Permissions permission) => permission != Permissions.None && (Permissions & permission) == permission;

    /// <summary>Host-initiated downgrade during a session (e.g. the local user switches to watch-only).</summary>
    public AccessGrant Revoke(Permissions permissions) => new(Mode, Permissions & ~permissions);

    public AccessGrant DowngradeToView() => Create(SessionMode.View, Permissions, Permissions);
}
