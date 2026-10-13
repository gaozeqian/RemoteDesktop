namespace RemoteDesktop.Domain;

/// <summary>Capabilities a controller may be granted on a host. Each feature is authorized independently.</summary>
[Flags]
public enum Permissions
{
    None = 0,
    ViewScreen = 1 << 0,
    ControlInput = 1 << 1,
    Clipboard = 1 << 2,
    Audio = 1 << 3,
    FileTransfer = 1 << 4,
    Terminal = 1 << 5,
    Camera = 1 << 6,

    /// <summary>
    /// Raising the secure attention sequence (Ctrl+Alt+Del) on the host.
    /// </summary>
    /// <remarks>
    /// Unlike the others this is not a policy choice: a host that cannot raise it simply never grants it (see
    /// <c>IInputInjector.SupportsSecureAttention</c>). The absence is what hides the controller's button, so the
    /// check lives in the grant rather than in the UI asking what platform the other end runs.
    /// </remarks>
    SecureAttention = 1 << 7,

    /// <summary>Everything that does not let the controller change state on the host.</summary>
    ViewOnly = ViewScreen | Audio,

    All = ViewScreen | ControlInput | Clipboard | Audio | FileTransfer | Terminal | Camera | SecureAttention,
}
