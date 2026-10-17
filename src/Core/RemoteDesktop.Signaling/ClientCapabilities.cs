namespace RemoteDesktop.Signaling;

/// <summary>
/// The bits a client sets in <c>Register.client_capabilities</c> to say which server messages it understands. The
/// server never sends a capability's messages to a device that did not declare it — that is the version negotiation:
/// an older client simply never receives an <c>AdminCommand</c>, and the console reports the refusal.
/// </summary>
public static class ClientCapabilities
{
    /// <summary>The client handles <c>ServerMessage.admin_command</c> (disconnect a session on operator request).</summary>
    public const uint AdminCommands = 1;

    /// <summary>Commands an <c>AdminCommand</c> may carry. Kept as strings so a new one is an addition, not a wire change.</summary>
    public static class Commands
    {
        /// <summary>Terminate the named session, as if the local user had clicked disconnect.</summary>
        public const string DisconnectSession = "disconnect_session";
    }
}
