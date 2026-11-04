namespace RemoteDesktop.Abstractions.Camera;

/// <summary>Whether this machine's desktop lets the controlled end use its cameras.</summary>
/// <remarks>
/// On Linux the answer is not only "the operating system's privacy setting": the camera portal asks for a system
/// access dialog, and a background host has no focused window to attach it to, so the dialog cannot be shown. The
/// host therefore has to be granted access explicitly, once, and this is the state that grant is in. On the other
/// platforms the system asks the person directly and there is no such state to remember, so no implementation is
/// registered there.
/// </remarks>
public enum CameraAccessState
{
    /// <summary>This platform has no separate camera grant; the operating system asks when the camera is opened.</summary>
    Unsupported,

    /// <summary>The state could not be read (no session bus, or the permission store did not answer).</summary>
    Unknown,

    /// <summary>Nobody has granted or refused camera access yet.</summary>
    Unset,

    /// <summary>Access was explicitly refused; the desktop will not ask again.</summary>
    Denied,

    /// <summary>Access was explicitly granted; the camera portal answers without asking anybody.</summary>
    Allowed,
}

/// <summary>
/// Reads and changes the controlled end's camera grant.
/// </summary>
/// <remarks>
/// This exists because of a desktop limitation rather than a product preference. See
/// <see cref="CameraAccessState"/> and <c>docs/usage/README.md</c> (the 摄像头 item in the Linux section).
/// </remarks>
public interface ICameraAccess
{
    Task<CameraAccessState> GetStateAsync(CancellationToken cancellationToken);

    /// <summary>Grants or refuses camera access for this process's desktop app id.</summary>
    /// <exception cref="InvalidOperationException">This process has no app id the portal would recognise.</exception>
    Task SetAllowedAsync(bool allowed, CancellationToken cancellationToken);
}
