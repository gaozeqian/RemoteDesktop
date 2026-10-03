using System.Collections.Concurrent;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Control;
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
    ILogger<HostSessionService> logger) : BackgroundService
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<SessionId, SessionContext> _sessions = [];

    public IReadOnlyCollection<SessionContext> ActiveSessions => [.. _sessions.Values];

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
                    control = await connection.AcceptChannelAsync(handshakeTimeout.Token).ConfigureAwait(false);
                    if (control?.Type != ChannelType.Control)
                    {
                        return;
                    }

                    session = await AuthenticateAsync(connection, control, handshakeTimeout.Token).ConfigureAwait(false);
                }

                if (session is null)
                {
                    return;
                }

                _sessions[session.Id] = session;
                LogSessionStarted(logger, session.Id, connection.RemoteDevice, session.Grant.Mode, session.Grant.Permissions, connection.Path);
                SessionStarted?.Invoke(this, new HostSessionEventArgs(session));
                try
                {
                    await RunSessionAsync(session, control, stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    _sessions.TryRemove(session.Id, out _);
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

        return new SessionContext(SessionId.New(), SessionRole.Host, connection, grant);
    }

    private async Task RunSessionAsync(SessionContext session, IChannel control, CancellationToken stoppingToken)
    {
        using var sessionEnd = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var channels = AcceptChannelsAsync(session, sessionEnd.Token);
        try
        {
            // The session lives as long as the control channel.
            while (await ControlMessages.ReadAsync(control, sessionEnd.Token).ConfigureAwait(false) is { } message)
            {
                if (message.BodyCase == ControlMessage.BodyOneofCase.Ping)
                {
                    var pong = new ControlMessage { Pong = new Pong { Timestamp = message.Ping.Timestamp } };
                    await ControlMessages.WriteAsync(control, pong, sessionEnd.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // Connection lost or the controller misbehaved: either way the session is over.
        }
        finally
        {
            await sessionEnd.CancelAsync().ConfigureAwait(false);
            await channels.ConfigureAwait(false);
        }
    }

    private async Task AcceptChannelsAsync(SessionContext session, CancellationToken cancellationToken)
    {
        var running = new List<Task>();
        try
        {
            while (await session.Connection.AcceptChannelAsync(cancellationToken).ConfigureAwait(false) is { } channel)
            {
                running.Add(DispatchAsync(channel, session, cancellationToken));
                running.RemoveAll(t => t.IsCompleted);
            }
        }
        catch (OperationCanceledException)
        {
        }

        await Task.WhenAll(running).ConfigureAwait(false);
    }

    private async Task DispatchAsync(IChannel channel, SessionContext session, CancellationToken cancellationToken)
    {
        try
        {
            var result = await dispatcher.DispatchAsync(channel, session, cancellationToken).ConfigureAwait(false);
            if (result != DispatchResult.Accepted)
            {
                LogChannelRefused(logger, session.Id, channel.Type, result);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // One failing feature must not end the whole session.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogChannelFailed(logger, session.Id, channel.Type, ex);
        }
    }

    private static ValueTask RefuseAsync(IChannel control, AuthFailure failure, CancellationToken cancellationToken) =>
        ControlMessages.WriteAsync(control, new ControlMessage { AuthResult = new AuthResult { Accepted = false, Failure = failure } }, cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} started: {Controller}, {Mode}, {Permissions}, {Path}.")]
    private static partial void LogSessionStarted(ILogger logger, SessionId sessionId, DeviceId controller, SessionMode mode, Permissions permissions, ConnectionPath path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} ended.")]
    private static partial void LogSessionEnded(ILogger logger, SessionId sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wrong password from {Controller}.")]
    private static partial void LogBadPassword(ILogger logger, DeviceId controller);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session handshake with {Controller} failed: {Reason}.")]
    private static partial void LogHandshakeFailed(ILogger logger, DeviceId controller, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: refused {Channel} channel ({Result}).")]
    private static partial void LogChannelRefused(ILogger logger, SessionId sessionId, ChannelType channel, DispatchResult result);

    [LoggerMessage(Level = LogLevel.Error, Message = "Session {SessionId}: {Channel} channel failed.")]
    private static partial void LogChannelFailed(ILogger logger, SessionId sessionId, ChannelType channel, Exception exception);
}
