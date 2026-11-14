using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Input;

namespace RemoteDesktop.Platform.Windows.Agent;

/// <summary>
/// The input injector inside the agent: ordinary input goes to <c>SendInput</c>, while Ctrl+Alt+Del is handed to the
/// host service through the agent link, because only a service may raise it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class AgentInputInjector(IInputInjector inner, AgentLink link) : IInputInjector
{
    public void Inject(InputEvent inputEvent)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);

        if (inputEvent is SecureAttentionEvent)
        {
            SendSecureAttentionSequence();
        }
        else
        {
            inner.Inject(inputEvent);
        }
    }

    /// <summary>True: this is the one injector that can, because it asks the service rather than trying to raise it itself.</summary>
    public bool SupportsSecureAttention => true;

    public void SendSecureAttentionSequence() =>
        link.RequestSecureAttentionAsync(CancellationToken.None).GetAwaiter().GetResult();
}
