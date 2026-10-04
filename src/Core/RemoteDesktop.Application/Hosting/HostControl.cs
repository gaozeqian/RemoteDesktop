using RemoteDesktop.Abstractions.Agent;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Application.Hosting;

/// <param name="Controller">Display form of the controller's device id.</param>
/// <param name="Mode"><c>control</c> or <c>view</c>.</param>
/// <param name="Path"><c>direct</c> or <c>relayed</c>.</param>
public sealed record HostSessionInfo(string Controller, string Mode, string Path);

/// <param name="DeviceId">Null until the host registered with the rendezvous server.</param>
/// <param name="ServerState">Offline, Connecting or Online.</param>
/// <param name="AgentState">What the session agent is doing (service only), e.g. <c>Running (Secure)</c>.</param>
/// <param name="IsService">True when the host runs in the background service rather than inside the UI.</param>
public sealed record HostStatus(
    int? DeviceId,
    string ServerState,
    string TemporaryPassword,
    IReadOnlyList<HostSessionInfo> Sessions,
    string? AgentState,
    bool IsService);

/// <summary>
/// What the UI needs from the "be controlled" side of this machine, whether that side runs in the UI process itself
/// or in the background service.
/// </summary>
public interface IHostControl
{
    Task<HostStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task RotateTemporaryPasswordAsync(CancellationToken cancellationToken);
}

/// <summary>Answers from the host components living in this process.</summary>
public sealed class LocalHostControl(
    IRendezvousClient rendezvous,
    HostCredentials credentials,
    HostSessionService sessions,
    IAgentGateway? agent,
    bool isService) : IHostControl
{
    public Task<HostStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var active = sessions.ActiveSessions.Select(s => new HostSessionInfo(
            s.Connection.RemoteDevice.ToDisplayString(),
            s.Grant.Mode == SessionMode.View ? "view" : "control",
            s.Connection.Path == Transport.ConnectionPath.Direct ? "direct" : "relayed")).ToList();

        var agentState = agent?.State is { } state
            ? state.Detail is { } detail ? $"{state.Status} ({state.Desktop}): {detail}" : $"{state.Status} ({state.Desktop})"
            : null;

        return Task.FromResult(new HostStatus(
            rendezvous.DeviceId?.Value,
            rendezvous.State.ToString(),
            credentials.TemporaryPassword,
            active,
            agentState,
            isService));
    }

    public Task RotateTemporaryPasswordAsync(CancellationToken cancellationToken)
    {
        credentials.RotateTemporaryPassword();
        return Task.CompletedTask;
    }
}
