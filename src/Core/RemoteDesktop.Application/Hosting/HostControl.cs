using RemoteDesktop.Abstractions.Agent;
using RemoteDesktop.Abstractions.Camera;
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
/// <param name="HasFixedPassword">Whether a fixed password for unattended access has been configured.</param>
/// <param name="RendezvousUri">The signaling server this device is configured to use, shown when it cannot be reached.</param>
/// <param name="CameraAccess">
/// Whether this host may use the cameras. Null on platforms whose operating system asks when a camera is opened;
/// on Linux it is the explicit grant the portal cannot ask a background host for.
/// </param>
public sealed record HostStatus(
    int? DeviceId,
    string ServerState,
    string TemporaryPassword,
    IReadOnlyList<HostSessionInfo> Sessions,
    string? AgentState,
    bool IsService,
    bool HasFixedPassword = false,
    string? RendezvousUri = null,
    CameraAccessState? CameraAccess = null);

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

    /// <summary>Stops one session's camera use, keeping the session.</summary>
    Task StopCameraAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Configures or clears the long-lived fixed password for unattended access.</summary>
    Task SetFixedPasswordAsync(string? password, CancellationToken cancellationToken);

    /// <summary>
    /// Grants or refuses the controlled end's use of its cameras, for platforms that need an explicit grant.
    /// </summary>
    /// <exception cref="NotSupportedException">This platform has no separate camera grant.</exception>
    /// <exception cref="InvalidOperationException">The grant could not be written; the message says why.</exception>
    Task SetCameraAccessAsync(bool allowed, CancellationToken cancellationToken);
}

/// <summary>Answers from the host components living in this process.</summary>
public sealed class LocalHostControl(
    IRendezvousClient rendezvous,
    HostCredentials credentials,
    HostSessionService sessions,
    IAgentGateway? agent,
    bool isService,
    ISecretStore? secrets = null,
    string? rendezvousUri = null,
    ICameraAccess? cameraAccess = null) : IHostControl
{
    public async Task<HostStatus> GetStatusAsync(CancellationToken cancellationToken)
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

        var camera = cameraAccess is null
            ? (CameraAccessState?)null
            : await cameraAccess.GetStateAsync(cancellationToken).ConfigureAwait(false);

        return new HostStatus(
            rendezvous.DeviceId?.Value,
            rendezvous.State.ToString(),
            credentials.TemporaryPassword,
            active,
            agentState,
            isService,
            !string.IsNullOrEmpty(credentials.FixedPassword),
            rendezvousUri,
            camera);
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

    public async Task StopCameraAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (SessionId.TryParse(sessionId, out var id))
        {
            await sessions.RevokeAsync(id, Permissions.Camera, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <exception cref="ArgumentException">The password is too short to be one; nothing is changed.</exception>
    public Task SetFixedPasswordAsync(string? password, CancellationToken cancellationToken)
    {
        var normalized = string.IsNullOrWhiteSpace(password) ? null : password.Trim();
        if (normalized is not null && !FixedPasswordPolicy.IsAcceptable(normalized))
        {
            throw new ArgumentException($"A fixed password must be at least {FixedPasswordPolicy.MinimumLength} characters.", nameof(password));
        }

        credentials.FixedPassword = normalized;
        if (secrets is not null)
        {
            FixedPasswordPolicy.Save(secrets, normalized);
        }

        return Task.CompletedTask;
    }

    public async Task SetCameraAccessAsync(bool allowed, CancellationToken cancellationToken)
    {
        if (cameraAccess is null)
        {
            throw new NotSupportedException("This platform has no separate camera access grant.");
        }

        await cameraAccess.SetAllowedAsync(allowed, cancellationToken).ConfigureAwait(false);
    }
}
