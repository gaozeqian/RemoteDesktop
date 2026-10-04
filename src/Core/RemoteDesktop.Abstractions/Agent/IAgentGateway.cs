using RemoteDesktop.Domain;

namespace RemoteDesktop.Abstractions.Agent;

/// <summary>Which desktop an agent works on.</summary>
public enum DesktopKind
{
    /// <summary>The interactive desktop of the logged-in user.</summary>
    User,

    /// <summary>Login screen, lock screen or UAC prompt: only a SYSTEM process can see and drive it.</summary>
    Secure,
}

/// <summary>Feature channels the service hands to the agent. Values are on the agent pipe; only append.</summary>
public enum AgentChannelKind : byte
{
    Video = 1,
    Input = 2,
    Clipboard = 3,
    Audio = 4,
    File = 5,
    Terminal = 6,
}

public enum AgentStatus
{
    Stopped,
    Starting,
    Running,

    /// <summary>No agent can run right now (nobody at the console, launch failed); the detail says why.</summary>
    Unavailable,
}

public sealed record AgentState(AgentStatus Status, DesktopKind Desktop, string? Detail = null);

public sealed class AgentUnavailableException : Exception
{
    public AgentUnavailableException()
    {
    }

    public AgentUnavailableException(string message)
        : base(message)
    {
    }

    public AgentUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The privileged host service cannot capture the screen or inject input itself (Session 0 on Windows, a daemon
/// elsewhere). It supervises an agent process inside the interactive session and reaches it through this port.
/// The agent is replaced when the input desktop changes (user ↔ login screen / UAC), which the gateway hides:
/// callers simply open a new channel.
/// </summary>
public interface IAgentGateway
{
    AgentState State { get; }

    event EventHandler? StateChanged;

    /// <summary>
    /// Opens a byte stream to the agent's implementation of a feature channel. Waits a short while for an agent that is
    /// starting; the stream ends when that agent exits. <paramref name="session"/> is the session the channel serves; the agent uses it to tell its log entries apart.
    /// </summary>
    /// <exception cref="AgentUnavailableException">No agent became ready in time.</exception>
    Task<Stream> OpenChannelAsync(AgentChannelKind kind, SessionId session, CancellationToken cancellationToken);
}
