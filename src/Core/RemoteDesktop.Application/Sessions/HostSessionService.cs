using System.Collections.Concurrent;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Control;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Application.Sessions;

public sealed class HostSessionEventArgs(SessionContext session) : EventArgs
{
    public SessionContext Session { get; } = session;
}

/// <summary>
/// Use case "be controlled": authenticates every incoming connection, computes the grant (watch mode included) and
/// serves the session's channels through the <see cref="ChannelDispatcher"/> until the controller leaves.
/// </summary>
public sealed partial class HostSessionService(
    IPeerListener listener,
    ChannelDispatcher dispatcher,
    HostCredentials credentials,
    AuthAttemptLimiter limiter,
    ConnectionAdmission admission,
    SessionTelemetry telemetry,
    SessionLifecycle lifecycle,
    ILogger<HostSessionService> logger,
    ISystemKeepAwake? keepAwake = null) : BackgroundService
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How many controllers may be in a session at once. Well past what a person connecting from a
    /// phone and a laptop needs, and low enough that opening sessions in a loop cannot spend the machine.</summary>
    private const int MaxSessions = 16;

    private readonly ConcurrentDictionary<SessionId, HostedSession> _hosted = [];

    public IReadOnlyCollection<SessionContext> ActiveSessions => [.. _hosted.Values.Select(h => h.Context)];

    public event EventHandler<HostSessionEventArgs>? SessionStarted;

    public event EventHandler<HostSessionEventArgs>? SessionEnded;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            IPeerConnection connection;
            try
            {
                connection = await listener.AcceptAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Half-open connections are cheap to start and expensive to hold; anything over the limit is dropped here.
            if (!admission.TryEnter())
            {
                admission.RecordRefusal();
                LogTooManyHandshakes(logger, connection.RemoteDevice, admission.InFlight, admission.Refused);
                await connection.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            _ = ServeAsync(connection, stoppingToken);
        }
    }

    private async Task ServeAsync(IPeerConnection connection, CancellationToken stoppingToken)
    {
        await using (connection.ConfigureAwait(false))
        {
            IChannel? control = null;
            try
            {
                SessionContext? session;
                using (var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                {
                    handshakeTimeout.CancelAfter(HandshakeTimeout);
                    try
                    {
                        control = await connection.AcceptChannelAsync(handshakeTimeout.Token).ConfigureAwait(false);
                        session = control?.Type == ChannelType.Control
                            ? await AuthenticateAsync(connection, control, handshakeTimeout.Token).ConfigureAwait(false)
                            : null;
                    }
                    finally
                    {
                        // The slot only covers the handshake; a session may last for hours.
                        admission.Release();
                    }
                }

                if (session is null)
                {
                    return;
                }

                // Every session holds its own channels and buffers, and each of those costs this machine memory for
                // as long as it lives. A password is all it takes to get this far, so without a ceiling one
                // controller — or somebody who was given the password — can open sessions until the machine gives up.
                if (_hosted.Count >= MaxSessions)
                {
                    LogTooManySessions(logger, connection.RemoteDevice, _hosted.Count);
                    return;
                }

                using var end = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var hosted = new HostedSession(session, control!, end);
                _hosted[session.Id] = hosted;
                LogSessionStarted(logger, session.Id, connection.RemoteDevice, session.Grant.Mode, session.Grant.Permissions, connection.Path);
                keepAwake?.Acquire();
                telemetry.Track(session, end.Token);
                lifecycle.Track(session, end.Token);
                SessionStarted?.Invoke(this, new HostSessionEventArgs(session));
                try
                {
                    await RunSessionAsync(hosted).ConfigureAwait(false);
                }
                finally
                {
                    _hosted.TryRemove(session.Id, out _);
                    hosted.Dispose();
                    keepAwake?.Release();
                    LogSessionEnded(logger, session.Id);
                    SessionEnded?.Invoke(this, new HostSessionEventArgs(session));
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or SessionException or OperationCanceledException)
            {
                LogHandshakeFailed(logger, connection.RemoteDevice, ex.Message);
            }
            finally
            {
                if (control is not null)
                {
                    await control.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<SessionContext?> AuthenticateAsync(IPeerConnection connection, IChannel control, CancellationToken cancellationToken)
    {
        var hello = (await ControlMessages.ExpectAsync(control, ControlMessage.BodyOneofCase.Hello, cancellationToken).ConfigureAwait(false)).Hello;

        var controllerVersion = new ProtocolVersion((ushort)hello.ProtocolMajor, (ushort)hello.ProtocolMinor);
        if (!ProtocolVersion.Current.IsCompatibleWith(controllerVersion))
        {
            await RefuseAsync(control, AuthFailure.IncompatibleVersion, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var controller = connection.RemoteDevice;
        if (limiter.IsBlocked(controller))
        {
            await RefuseAsync(control, AuthFailure.TooManyAttempts, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var nonce = RandomNumberGenerator.GetBytes(PasswordProof.NonceSize);
        await ControlMessages.WriteAsync(control, new ControlMessage
        {
            AuthChallenge = new AuthChallenge
            {
                Nonce = ByteString.CopyFrom(nonce),
                ProtocolMajor = ProtocolVersion.Current.Major,
                ProtocolMinor = ProtocolVersion.Current.Minor,
            },
        }, cancellationToken).ConfigureAwait(false);

        var response = (await ControlMessages.ExpectAsync(control, ControlMessage.BodyOneofCase.AuthResponse, cancellationToken)
            .ConfigureAwait(false)).AuthResponse;

        // Fingerprints come from the pinned TLS certificates, binding the proof to this very connection.
        if (!credentials.Verify(response.Proof.Span, nonce, connection.LocalFingerprint, connection.RemoteFingerprint))
        {
            limiter.RecordFailure(controller);
            LogBadPassword(logger, controller);
            await RefuseAsync(control, AuthFailure.BadPassword, cancellationToken).ConfigureAwait(false);
            return null;
        }

        limiter.RecordSuccess(controller);
        var grant = AccessGrant.Create(
            ControlMessages.FromMessage(hello.Mode),
            ControlMessages.FromWire(hello.RequestedPermissions),
            credentials.AllowedPermissions);

        await ControlMessages.WriteAsync(control, new ControlMessage
        {
            AuthResult = new AuthResult
            {
                Accepted = true,
                Mode = ControlMessages.ToMessage(grant.Mode),
                GrantedPermissions = (uint)grant.Permissions,
            },
        }, cancellationToken).ConfigureAwait(false);

        // The controller's id, adopted so both ends report under one session id. A collision — a controller picking an
        // id another session already uses — falls back to a fresh one, which is the only case where the two ends
        // disagree, and it is visible in the console as two rows rather than as one wrong one.
        var id = SessionId.TryParse(hello.SessionId, out var announced) && !_hosted.ContainsKey(announced)
            ? announced
            : SessionId.New();

        return new SessionContext(id, SessionRole.Host, connection, grant);
    }

    private async Task RunSessionAsync(HostedSession hosted)
    {
        using var sessionEnd = CancellationTokenSource.CreateLinkedTokenSource(hosted.End.Token);
        var channels = AcceptChannelsAsync(hosted, sessionEnd.Token);
        try
        {
            // The session lives as long as the control channel.
            while (await ControlMessages.ReadAsync(hosted.Control, sessionEnd.Token).ConfigureAwait(false) is { } message)
            {
                if (message.BodyCase == ControlMessage.BodyOneofCase.Ping)
                {
                    var pong = new ControlMessage { Pong = new Pong { Timestamp = message.Ping.Timestamp } };
                    await hosted.WriteControlAsync(pong, sessionEnd.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
        {
            // Connection lost or the controller misbehaved: either way the session is over.
        }
        finally
        {
            await sessionEnd.CancelAsync().ConfigureAwait(false);
            await channels.ConfigureAwait(false);
        }
    }

    private async Task AcceptChannelsAsync(HostedSession hosted, CancellationToken cancellationToken)
    {
        var running = new List<Task>();
        try
        {
            while (await hosted.Context.Connection.AcceptChannelAsync(cancellationToken).ConfigureAwait(false) is { } channel)
            {
                running.Add(DispatchAsync(channel, hosted, cancellationToken));
                running.RemoveAll(t => t.IsCompleted);
            }
        }
        catch (OperationCanceledException)
        {
        }

        await Task.WhenAll(running).ConfigureAwait(false);
    }

    private async Task DispatchAsync(IChannel channel, HostedSession hosted, CancellationToken cancellationToken)
    {
        var session = hosted.Context;

        // Its own token, so that revoking a permission can end this channel without ending the session.
        using var channelEnd = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        hosted.Add(channel.Type, channelEnd);
        try
        {
            var result = await dispatcher.DispatchAsync(channel, session, channelEnd.Token).ConfigureAwait(false);
            if (result != DispatchResult.Accepted)
            {
                LogChannelRefused(logger, session.Id, channel.Type, result);
            }
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // One failing feature must not end the whole session.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogChannelFailed(logger, session.Id, channel.Type, ex);
        }
        finally
        {
            hosted.Remove(channelEnd);
        }
    }

    /// <summary>
    /// Takes away everything but watching from a running session: the grant becomes view-only, channels that are no
    /// longer allowed are closed, and the controller is told so its UI follows. Enforcement does not depend on that.
    /// </summary>
    /// <returns><c>false</c> when there is no such session.</returns>
    public async Task<bool> DowngradeToViewAsync(SessionId id, CancellationToken cancellationToken)
    {
        if (!_hosted.TryGetValue(id, out var hosted))
        {
            return false;
        }

        hosted.Context.Grant = hosted.Context.Grant.DowngradeToView();
        LogDowngraded(logger, id);
        await ApplyGrantAsync(hosted, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Takes a permission away from a running session (for example the camera): the channels that need it are closed and
    /// the controller is told so its UI follows. The session itself goes on.
    /// </summary>
    /// <returns><c>false</c> when there is no such session.</returns>
    public async Task<bool> RevokeAsync(SessionId id, Permissions permissions, CancellationToken cancellationToken)
    {
        if (!_hosted.TryGetValue(id, out var hosted))
        {
            return false;
        }

        hosted.Context.Grant = hosted.Context.Grant.Revoke(permissions);
        LogRevoked(logger, id, permissions);
        await ApplyGrantAsync(hosted, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Takes a permission away from every running session.</summary>
    public async Task RevokeFromAllAsync(Permissions permissions, CancellationToken cancellationToken)
    {
        foreach (var id in _hosted.Keys)
        {
            await RevokeAsync(id, permissions, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Whether the session has a channel of this type open right now (e.g. the camera is streaming).</summary>
    public bool IsChannelOpen(SessionId id, ChannelType type) => _hosted.TryGetValue(id, out var hosted) && hosted.HasOpen(type);

    // Closes what the new grant no longer covers and tells the controller.
    private async Task ApplyGrantAsync(HostedSession hosted, CancellationToken cancellationToken)
    {
        var session = hosted.Context;
        hosted.Cancel(type => dispatcher.GetRequiredPermission(type) is { } required && !session.Grant.Allows(required));
        try
        {
            await hosted.WriteControlAsync(new ControlMessage
            {
                GrantChanged = new GrantChanged { Mode = ControlMessages.ToMessage(session.Grant.Mode), GrantedPermissions = (uint)session.Grant.Permissions },
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The controller is already gone; the change stands.
        }
    }

    /// <summary>Ends a running session.</summary>
    /// <returns><c>false</c> when there is no such session.</returns>
    public bool Disconnect(SessionId id)
    {
        if (!_hosted.TryGetValue(id, out var hosted))
        {
            return false;
        }

        LogDisconnected(logger, id);
        hosted.End.Cancel();
        return true;
    }

    private static ValueTask RefuseAsync(IChannel control, AuthFailure failure, CancellationToken cancellationToken) =>
        ControlMessages.WriteAsync(control, new ControlMessage { AuthResult = new AuthResult { Accepted = false, Failure = failure } }, cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} started: {Controller}, {Mode}, {Permissions}, {Path}.")]
    private static partial void LogSessionStarted(ILogger logger, SessionId sessionId, DeviceId controller, SessionMode mode, Permissions permissions, ConnectionPath path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} switched to watch-only by the host.")]
    private static partial void LogDowngraded(ILogger logger, SessionId sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: {Permissions} taken away by the local user.")]
    private static partial void LogRevoked(ILogger logger, SessionId sessionId, Permissions permissions);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} disconnected by the host.")]
    private static partial void LogDisconnected(ILogger logger, SessionId sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} ended.")]
    private static partial void LogSessionEnded(ILogger logger, SessionId sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wrong password from {Controller}.")]
    private static partial void LogBadPassword(ILogger logger, DeviceId controller);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session handshake with {Controller} failed: {Reason}.")]
    private static partial void LogHandshakeFailed(ILogger logger, DeviceId controller, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a connection from {Controller}: {InFlight} other connections are still in their handshake ({Refused} refused so far).")]
    private static partial void LogTooManyHandshakes(ILogger logger, DeviceId controller, int inFlight, long refused);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a connection from {Controller}: {Active} sessions are already running.")]
    private static partial void LogTooManySessions(ILogger logger, DeviceId controller, int active);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: refused {Channel} channel ({Result}).")]
    private static partial void LogChannelRefused(ILogger logger, SessionId sessionId, ChannelType channel, DispatchResult result);

    [LoggerMessage(Level = LogLevel.Error, Message = "Session {SessionId}: {Channel} channel failed.")]
    private static partial void LogChannelFailed(ILogger logger, SessionId sessionId, ChannelType channel, Exception exception);
}
