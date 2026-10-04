using RemoteDesktop.Abstractions.Agent;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Application.Hosting;

/// <param name="Id">Identifies the session for <see cref="IHostControl.DowngradeSessionAsync"/> and <see cref="IHostControl.DisconnectSessionAsync"/>.</param>
/// <param name="Controller">Display form of the controller's device id.</param>
/// <param name="Mode"><c>control</c> or <c>view</c>.</param>
/// <param name="Path"><c>direct</c> or <c>relayed</c>.</param>
/// <param name="CameraActive">The controller is looking through this computer's camera right now.</param>
public sealed record HostSessionInfo(string Id, string Controller, string Mode, string Path, bool CameraActive = false);

/// <param name="DeviceId">Null until the host registered with the rendezvous server.</param>
/// <param name="ServerState">Offline, Connecting or Online.</param>
/// <param name="AgentState">What the session agent is doing (service only), e.g. <c>Running (Secure)</c>.</param>
/// <param name="IsService">True when the host runs in the background service rather than inside the UI.</param>
/// <param name="CameraAllowed">The person at this computer let controllers use the camera (off until they say so).</param>
public sealed record HostStatus(
    int? DeviceId,
    string ServerState,
    string TemporaryPassword,
    IReadOnlyList<HostSessionInfo> Sessions,
    string? AgentState,
    bool IsService,
    bool CameraAllowed = false);

/// <summary>
/// What the UI needs from the "be controlled" side of this machine, whether that side runs in the UI process itself
/// or in the background service.
/// </summary>
public interface IHostControl
{
    Task<HostStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task RotateTemporaryPasswordAsync(CancellationToken cancellationToken);

    /// <summary>Switches a running session to watch-only: input, clipboard, files and the like stop at once.</summary>
    Task DowngradeSessionAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Ends a running session.</summary>
    Task DisconnectSessionAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Lets controllers use this computer's camera, or not. Turning it off ends camera use in running sessions at once.</summary>
    Task SetCameraAllowedAsync(bool allowed, CancellationToken cancellationToken);

    /// <summary>Stops one session's camera use, keeping the session.</summary>
    Task StopCameraAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>Answers from the host components living in this process.</summary>
public sealed class LocalHostControl(
    IRendezvousClient rendezvous,
    HostCredentials credentials,
    HostSessionService sessions,
    IAgentGateway? agent,
    bool isService,
    ISecretStore? secrets = null) : IHostControl
{
    public Task<HostStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var active = sessions.ActiveSessions.Select(s => new HostSessionInfo(
            s.Id.ToString(),
            s.Connection.RemoteDevice.ToDisplayString(),
            s.Grant.Mode == SessionMode.View ? "view" : "control",
            s.Connection.Path == Transport.ConnectionPath.Direct ? "direct" : "relayed",
            sessions.IsChannelOpen(s.Id, ChannelType.Camera))).ToList();

        var agentState = agent?.State is { } state
            ? state.Detail is { } detail ? $"{state.Status} ({state.Desktop}): {detail}" : $"{state.Status} ({state.Desktop})"
            : null;

        return Task.FromResult(new HostStatus(
            rendezvous.DeviceId?.Value,
            rendezvous.State.ToString(),
            credentials.TemporaryPassword,
            active,
            agentState,
            isService,
            credentials.AllowedPermissions.HasFlag(Permissions.Camera)));
    }

    public Task RotateTemporaryPasswordAsync(CancellationToken cancellationToken)
    {
        credentials.RotateTemporaryPassword();
        return Task.CompletedTask;
    }

    public async Task DowngradeSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (SessionId.TryParse(sessionId, out var id))
        {
            await sessions.DowngradeToViewAsync(id, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task DisconnectSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (SessionId.TryParse(sessionId, out var id))
        {
            sessions.Disconnect(id);
        }

        return Task.CompletedTask;
    }

    public async Task SetCameraAllowedAsync(bool allowed, CancellationToken cancellationToken)
    {
        credentials.AllowedPermissions = HostPolicy.WithCamera(credentials.AllowedPermissions, allowed);
        if (secrets is not null)
        {
            HostPolicy.SaveCameraAllowed(secrets, allowed);
        }

        if (!allowed)
        {
            await sessions.RevokeFromAllAsync(Permissions.Camera, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopCameraAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (SessionId.TryParse(sessionId, out var id))
        {
            await sessions.RevokeAsync(id, Permissions.Camera, cancellationToken).ConfigureAwait(false);
        }
    }
}
