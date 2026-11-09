namespace RemoteDesktop.Abstractions.Input;

public interface IInputInjector
{
    void Inject(InputEvent inputEvent);

    /// <summary>Injects as one remote session. Platforms with shared OS input state may use this for arbitration.</summary>
    void Inject(InputEvent inputEvent, string sessionId) => Inject(inputEvent);

    /// <summary>Releases any session-level input ownership. The channel still sends its pressed-key releases first.</summary>
    void ReleaseSession(string sessionId)
    {
    }

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
