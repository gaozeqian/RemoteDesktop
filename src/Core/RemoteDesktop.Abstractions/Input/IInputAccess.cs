namespace RemoteDesktop.Abstractions.Input;

/// <summary>How the controlled end's operating system stands on letting this process inject input.</summary>
/// <remarks>
/// The session grant answers a different question: it is the host's policy about whether a controller may control at
/// all. On Wayland there is one more answer between policy and a key arriving, because the desktop portal asks the
/// person at the host before it will accept synthetic input. That answer used to be invisible to the controller:
/// a declined dialog looked exactly like a broken session. This is the state that closes that gap.
/// </remarks>
public enum InputAccessState
{
    /// <summary>Nothing has said what the state is; treat it as unavailable to report rather than as a refusal.</summary>
    Unknown,

    /// <summary>This platform has no separate grant; input works whenever the session grant allows it.</summary>
    NotApplicable,

    /// <summary>The grant has not been requested yet. On Linux the desktop asks on the first input.</summary>
    NotRequested,

    /// <summary>The desktop is showing, or is about to show, its permission question.</summary>
    Requesting,

    /// <summary>The desktop allows synthetic input.</summary>
    Granted,

    /// <summary>The person at the host refused, or the desktop denied the request.</summary>
    Denied,

    /// <summary>The mechanism used to inject input is not available (portal, session bus or libei), with a reason.</summary>
    Unavailable,
}

/// <param name="Reason">What the platform said, when it said anything; shown after the controller's own sentence.</param>
public sealed record InputAccessStatus(InputAccessState State, string? Reason = null)
{
    public static InputAccessStatus Unknown { get; } = new(InputAccessState.Unknown);

    public static InputAccessStatus NotApplicable { get; } = new(InputAccessState.NotApplicable);
}

/// <summary>
/// Reports, and can re-request, the operating system's permission for injecting input on this host.
/// </summary>
/// <remarks>
/// Implementations exist only where the operating system keeps a grant separate from the session's. A platform
/// without one registers nothing, and the controller then shows no control-authorization banner. The status is a
/// host-wide fact rather than a per-session one: the same portal grant, the same descriptor, and one dialog.
/// </remarks>
public interface IInputAccess
{
    /// <summary>The current state. Safe to read from any thread.</summary>
    InputAccessStatus Status { get; }

    /// <summary>Raised whenever <see cref="Status"/> changes, possibly on any thread.</summary>
    event EventHandler<InputAccessStatus>? StatusChanged;

    /// <summary>
    /// Asks the operating system again, ignoring the backoff a previous refusal left behind.
    /// </summary>
    /// <remarks>
    /// This is what a controller's "retry" action calls. It does not throw when the desktop refuses: a refusal is a
    /// returned state, not an error. Cancellation is the only reason to throw.
    /// </remarks>
    Task<InputAccessStatus> RequestAsync(CancellationToken cancellationToken);
}
