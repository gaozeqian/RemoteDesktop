using Google.Protobuf;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Control;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Application.Sessions;

/// <summary>Use case "connect to a remote device": transport connection, version negotiation, password proof.</summary>
public sealed class ControllerSessionService(
    IPeerConnector connector,
    SessionTelemetry telemetry,
    SessionLifecycle lifecycle,
    TimeProvider time)
{
    /// <exception cref="PeerConnectException">No connection could be established.</exception>
    /// <exception cref="SessionException">The host refused the session.</exception>
    public async Task<RemoteSession> ConnectAsync(DeviceId target, string password, SessionMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);

        // Minted here rather than after the handshake: the host adopts this id so both ends report telemetry under it,
        // and one session stays one row in the console.
        var sessionId = SessionId.New();

        var connection = await connector.ConnectAsync(target, cancellationToken).ConfigureAwait(false);
        IChannel? control = null;
        try
        {
            control = await connection.OpenChannelAsync(ChannelType.Control, bidirectional: true, cancellationToken).ConfigureAwait(false);

            var requested = mode == SessionMode.View ? Permissions.ViewOnly : Permissions.All;
            await ControlMessages.WriteAsync(control, new ControlMessage
            {
                Hello = new Hello
                {
                    ProtocolMajor = ProtocolVersion.Current.Major,
                    ProtocolMinor = ProtocolVersion.Current.Minor,
                    Mode = ControlMessages.ToMessage(mode),
                    RequestedPermissions = (uint)requested,
                    SessionId = sessionId.ToString(),
                },
            }, cancellationToken).ConfigureAwait(false);

            var challenge = (await ControlMessages.ExpectAsync(control, ControlMessage.BodyOneofCase.AuthChallenge, cancellationToken)
                .ConfigureAwait(false)).AuthChallenge;
            var hostVersion = ProtocolVersion.FromWire(challenge.ProtocolMajor, challenge.ProtocolMinor);
            if (!ProtocolVersion.Current.IsCompatibleWith(hostVersion))
            {
                throw new SessionException(SessionFailure.IncompatibleVersion, $"The remote device speaks protocol {hostVersion}.");
            }

            // Version 3 defines exactly two credential slots. Refusing any other count bounds the work an untrusted
            // host can make this side do before it has proved anything.
            if (challenge.Credentials.Count != HostCredentials.CredentialSlotCount)
            {
                throw new SessionException(SessionFailure.ProtocolError, "The remote device sent an invalid password challenge.");
            }

            var attempts = new List<PasswordAttempt>(challenge.Credentials.Count);
            var response = new AuthResponse();
            foreach (var credential in challenge.Credentials)
            {
                if (credential.Salt.Length < PasswordAuthenticator.SaltSize
                    || credential.ServerPublic.IsEmpty
                    || credential.ServerPublic.Length > PasswordAuthenticator.MaxPublicValueSize)
                {
                    throw new SessionException(SessionFailure.ProtocolError, "The remote device sent an invalid password challenge.");
                }

                PasswordAttempt attempt;
                try
                {
                    attempt = PasswordAuthenticator.Respond(
                        password,
                        credential.Salt.Span,
                        credential.ServerPublic.Span,
                        connection.RemoteFingerprint,
                        connection.LocalFingerprint);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Org.BouncyCastle.Crypto.CryptoException)
                {
                    throw new SessionException(SessionFailure.ProtocolError, "The remote device sent an invalid password challenge.");
                }

                attempts.Add(attempt);
                response.Credentials.Add(new SrpResponse
                {
                    ClientPublic = ByteString.CopyFrom(attempt.ClientPublic),
                    ClientEvidence = ByteString.CopyFrom(attempt.ClientEvidence),
                });
            }

            await ControlMessages.WriteAsync(control, new ControlMessage { AuthResponse = response }, cancellationToken).ConfigureAwait(false);

            var result = (await ControlMessages.ExpectAsync(control, ControlMessage.BodyOneofCase.AuthResult, cancellationToken)
                .ConfigureAwait(false)).AuthResult;
            if (!result.Accepted)
            {
                throw SessionException.FromAuthFailure(result.Failure);
            }

            // A matching TLS fingerprint only says signaling introduced this peer. SRP's server evidence is what
            // proves the peer actually knows the password; without checking it, a malicious signaling server could
            // impersonate the host and collect the client's evidence.
            if (result.CredentialIndex >= attempts.Count
                || !PasswordAuthenticator.VerifyServer(attempts[(int)result.CredentialIndex], result.ServerEvidence.Span))
            {
                throw new SessionException(SessionFailure.ProtocolError, "The remote device could not prove it knows the password.");
            }

            // The host decided the grant; the controller only mirrors it (e.g. to hide unavailable features).
            var grantedMode = ControlMessages.FromMessage(result.Mode);
            var granted = ControlMessages.FromWire(result.GrantedPermissions);
            var context = new SessionContext(sessionId, SessionRole.Controller, connection, AccessGrant.Create(grantedMode, granted, granted));
            var session = RemoteSession.Start(context, control, time);
            telemetry.Track(session);
            lifecycle.Track(session);
            return session;
        }
        catch
        {
            if (control is not null)
            {
                await control.DisposeAsync().ConfigureAwait(false);
            }

            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
