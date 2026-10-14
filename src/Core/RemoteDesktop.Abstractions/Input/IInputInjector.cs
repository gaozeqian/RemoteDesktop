namespace RemoteDesktop.Abstractions.Input;

public interface IInputInjector
{
    void Inject(InputEvent inputEvent);

    /// <summary>
    /// Whether <see cref="SendSecureAttentionSequence"/> can actually do anything here.
    /// </summary>
    /// <remarks>
    /// Not every implementation can, and the split is not "Windows versus the rest": on Windows the in-process
    /// injector refuses it too, because raising Ctrl+Alt+Del needs the privileged host service and only the agent —
    /// which asks that service — can. The host copies this into the session's grant, so a controller hides the button
    /// where it would be dead instead of offering it and doing nothing.
    /// </remarks>
    bool SupportsSecureAttention { get; }

    /// <summary>Secure attention sequence (Ctrl+Alt+Del); requires the privileged host service on Windows.</summary>
    /// <exception cref="NotSupportedException">When <see cref="SupportsSecureAttention"/> is false.</exception>
    void SendSecureAttentionSequence();
}
