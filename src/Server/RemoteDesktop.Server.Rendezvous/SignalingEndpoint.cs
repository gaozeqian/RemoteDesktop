using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using RemoteDesktop.Server.Storage;
using RemoteDesktop.Signaling;
using RemoteDesktop.Signaling.Messages;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>
/// One WebSocket per online device. The device first proves it owns its certificate key by signing a nonce, so no
/// one can register under (and receive connection requests for) another device's id.
/// </summary>
public sealed partial class SignalingEndpoint(
    IDeviceRegistry registry,
    DevicePresence presence,
    PresenceService presenceQueries,
    ConnectionBroker broker,
    AddressReflector reflector,
    ConnectionQuota quota,
    KeyedTokenBucket<IPAddress> registrationBudget,
    ServerMetrics metrics,
    TelemetryReceiver telemetry,
    ILogger<SignalingEndpoint> logger)
{
    private static readonly TimeSpan RegisterTimeout = TimeSpan.FromSeconds(10);

    public async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Refused before the upgrade: the socket is never turned into the server's to keep.
        var address = context.Connection.RemoteIpAddress;
        if (!quota.TryOpen(address))
        {
            metrics.ConnectionRefused();
            LogOverQuota(logger, address?.ToString() ?? "unknown", quota.Open);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var cancellationToken = context.RequestAborted;
            try
            {
                using var session = await RegisterAsync(socket, address, cancellationToken).ConfigureAwait(false);
                if (session is null)
                {
                    await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "registration failed").ConfigureAwait(false);
                    return;
                }

                presence.Add(session);
                try
                {
                    await ServeAsync(socket, session, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    presence.Remove(session);
                    presenceQueries.Forget(session);
                    LogDisconnected(logger, session.DeviceId);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidDataException)
            {
                // Client went away or sent garbage; nothing to clean up beyond presence.
            }
        }
        finally
        {
            quota.Close(address);
        }
    }

    private async Task<DeviceSession?> RegisterAsync(WebSocket socket, IPAddress? address, CancellationToken cancellationToken)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        await WebSocketMessaging.SendAsync(
            socket,
            new ServerMessage { RegisterChallenge = new RegisterChallenge { Nonce = ByteString.CopyFrom(nonce) } },
            cancellationToken).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RegisterTimeout);
        var message = await WebSocketMessaging.ReceiveAsync(socket, ClientMessage.Parser, timeout.Token).ConfigureAwait(false);
        if (message?.BodyCase != ClientMessage.BodyOneofCase.Register)
        {
            return null;
        }

        // Before the certificate is checked and before the registry is written to: a registration costs this server an
        // identity, and identities are the unit every other budget here is counted in.
        // Counted under the same key the connection quota uses (an IPv6 /64, an IPv4-mapped address as IPv4), so one
        // machine cannot get a fresh budget by changing addresses inside its own network. The refund below must use the
        // same key or it would credit a bucket nothing was taken from.
        var budgetKey = ConnectionQuota.Normalize(address);
        if (budgetKey is not null && !registrationBudget.TryTake(budgetKey))
        {
            metrics.RegistrationRefused();
            LogThrottledRegistration(logger, address!.ToString());
            return null;
        }

        var fingerprint = VerifyRegistration(message.Register, nonce);
        if (fingerprint is null)
        {
            // Worth a line of its own: a run of these is what somebody guessing at keys looks like from here.
            LogRefusedRegistration(logger, address?.ToString() ?? "unknown");
            return null;
        }

        var registration = await registry.GetOrAllocateAsync(fingerprint, cancellationToken).ConfigureAwait(false);

        // A revoked device proves its key like anyone else, but the id it proves has been taken out of service: the
        // server must not hand it (back) out. Refused before the Registered message, so the device learns nothing new.
        if (await registry.IsRevokedAsync(registration.DeviceId, cancellationToken).ConfigureAwait(false))
        {
            LogRefusedRevoked(logger, registration.DeviceId);
            return null;
        }

        // Counted once the identity exists, which is the point this server has spent something on the device, whether
        // the identity is new or one the registry already had.
        metrics.Registered();
        if (!registration.Created && budgetKey is not null)
        {
            // A known device coming back after a dropped connection, a sleep or a server restart. It proved its key,
            // and it is not what the budget is there to slow down, so the token goes back.
            registrationBudget.Refund(budgetKey);
        }

        var session = new DeviceSession(registration.DeviceId, fingerprint, message.Register.ClientCapabilities, socket);
        await session.SendAsync(
            new ServerMessage { Registered = new Registered { DeviceId = registration.DeviceId, ReflectorPort = (uint)reflector.Port } },
            cancellationToken).ConfigureAwait(false);

        LogRegistered(logger, registration.DeviceId);
        return session;
    }

    /// <returns>The certificate fingerprint, or <c>null</c> when the proof is invalid.</returns>
    private static string? VerifyRegistration(Register register, byte[] nonce)
    {
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(register.Certificate.Span);
            using var key = certificate.GetECDsaPublicKey();
            if (key is null || !key.VerifyData(nonce, register.Signature.Span, HashAlgorithmName.SHA256))
            {
                return null;
            }

            return certificate.GetCertHashString(HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private async Task ServeAsync(WebSocket socket, DeviceSession session, CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await WebSocketMessaging.ReceiveAsync(socket, ClientMessage.Parser, cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                await CloseAsync(socket, WebSocketCloseStatus.NormalClosure, null).ConfigureAwait(false);
                return;
            }

            try
            {
                await HandleMessageAsync(session, message, cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // One bad message must not take down the device's whole connection.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogMessageFailed(logger, session.DeviceId, ex);
            }
        }
    }

    private async Task HandleMessageAsync(DeviceSession session, ClientMessage message, CancellationToken cancellationToken)
    {
        switch (message.BodyCase)
        {
            case ClientMessage.BodyOneofCase.ConnectRequest:
                await broker.HandleConnectRequestAsync(session, message.ConnectRequest, cancellationToken).ConfigureAwait(false);
                break;
            case ClientMessage.BodyOneofCase.ConnectAccept:
                await broker.HandleAcceptAsync(session, message.ConnectAccept, cancellationToken).ConfigureAwait(false);
                break;
            case ClientMessage.BodyOneofCase.ConnectReject:
                await broker.HandleRejectAsync(session, message.ConnectReject, cancellationToken).ConfigureAwait(false);
                break;
            case ClientMessage.BodyOneofCase.PresenceQuery:
                await presenceQueries.HandleQueryAsync(session, message.PresenceQuery, cancellationToken).ConfigureAwait(false);
                break;
            case ClientMessage.BodyOneofCase.Telemetry:
                // Fire-and-forget, and synchronous on purpose: it validates, rate-limits and queues, and the queue
                // drops rather than waits, so a flood of heartbeats cannot hold this loop.
                telemetry.Handle(session, message.Telemetry);
                break;
            case ClientMessage.BodyOneofCase.SessionEnded:
                // The only end fact a direct session has; same fire-and-forget contract as a heartbeat.
                telemetry.HandleSessionEnded(session, message.SessionEnded);
                break;
            default:
                // Unknown or out-of-order message: ignore so newer clients keep working.
                break;
        }
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string? reason)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await socket.CloseOutputAsync(status, reason, CancellationToken.None).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {DeviceId} registered.")]
    private static partial void LogRegistered(ILogger logger, uint deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {DeviceId} disconnected.")]
    private static partial void LogDisconnected(ILogger logger, uint deviceId);

    // The three refusals below carry the caller's address: they are the only place this server writes one down, and
    // they are exactly the lines an operator needs when somebody is abusing the server. See docs/design/README.md.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a connection from {Address}: {Open} are already being served.")]
    private static partial void LogOverQuota(ILogger logger, string address, int open);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Throttled registrations from {Address}.")]
    private static partial void LogThrottledRegistration(ILogger logger, string address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a registration from {Address}: the certificate proof did not verify.")]
    private static partial void LogRefusedRegistration(ILogger logger, string address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a registration from device {DeviceId}: it has been revoked.")]
    private static partial void LogRefusedRevoked(ILogger logger, uint deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device {DeviceId}: a message handler failed; the connection continues.")]
    private static partial void LogMessageFailed(ILogger logger, uint deviceId, Exception exception);
}
