using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
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
    ConnectionBroker broker,
    AddressReflector reflector,
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

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var cancellationToken = context.RequestAborted;
        try
        {
            using var session = await RegisterAsync(socket, cancellationToken).ConfigureAwait(false);
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
                LogDisconnected(logger, session.DeviceId);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidDataException)
        {
            // Client went away or sent garbage; nothing to clean up beyond presence.
        }
    }

    private async Task<DeviceSession?> RegisterAsync(WebSocket socket, CancellationToken cancellationToken)
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

        var fingerprint = VerifyRegistration(message.Register, nonce);
        if (fingerprint is null)
        {
            return null;
        }

        var deviceId = await registry.GetOrAllocateAsync(fingerprint, cancellationToken).ConfigureAwait(false);
        var session = new DeviceSession(deviceId, fingerprint, socket);
        await session.SendAsync(
            new ServerMessage { Registered = new Registered { DeviceId = deviceId, ReflectorPort = (uint)reflector.Port } },
            cancellationToken).ConfigureAwait(false);

        LogRegistered(logger, deviceId);
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
            switch (message?.BodyCase)
            {
                case null:
                    await CloseAsync(socket, WebSocketCloseStatus.NormalClosure, null).ConfigureAwait(false);
                    return;
                case ClientMessage.BodyOneofCase.ConnectRequest:
                    await broker.HandleConnectRequestAsync(session, message.ConnectRequest, cancellationToken).ConfigureAwait(false);
                    break;
                case ClientMessage.BodyOneofCase.ConnectAccept:
                    await broker.HandleAcceptAsync(session, message.ConnectAccept, cancellationToken).ConfigureAwait(false);
                    break;
                case ClientMessage.BodyOneofCase.ConnectReject:
                    await broker.HandleRejectAsync(session, message.ConnectReject, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    // Unknown or out-of-order message: ignore so newer clients keep working.
                    break;
            }
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
}
