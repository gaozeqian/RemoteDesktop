namespace RemoteDesktop.Abstractions.Input;

public interface IInputInjector
{
    void Inject(InputEvent inputEvent);

    /// <summary>Secure attention sequence (Ctrl+Alt+Del); requires the privileged host service on Windows.</summary>
    void SendSecureAttentionSequence();
}
