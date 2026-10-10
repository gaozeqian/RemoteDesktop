using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using RemoteDesktop.Signaling;
using RemoteDesktop.Signaling.Messages;
using RemoteDesktop.Transport.Identity;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// The signaling handshake the way a bare client speaks it: a WebSocket and the two messages registration takes.
/// The product's own client is what every other test uses; this exists for the tests that have to go around it —
/// a server with limits the client would sit inside, or a server whose certificate only the test knows about.
/// </summary>
public static class RawSignaling
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<ClientWebSocket> ConnectAsync(Uri uri, RemoteCertificateValidationCallback? validate = null)
    {
        var socket = new ClientWebSocket();
        if (validate is not null)
        {
            socket.Options.RemoteCertificateValidationCallback = validate;
        }

        try
        {
            await socket.ConnectAsync(uri, Ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <returns>The assigned device id, or <c>null</c> when the server refused the registration.</returns>
    public static async Task<uint?> RegisterAsync(Uri uri)
    {
        using var socket = await ConnectAsync(uri);
        return await RegisterAsync(socket);
    }

    /// <returns>The assigned device id, or <c>null</c> when the server refused the registration.</returns>
    public static async Task<uint?> RegisterAsync(WebSocket socket)
    {
        using var identity = DeviceIdentity.CreateEphemeral();
        var challenge = await WebSocketMessaging.ReceiveAsync(socket, ServerMessage.Parser, Ct);
        if (challenge?.BodyCase != ServerMessage.BodyOneofCase.RegisterChallenge)
        {
            return null;
        }

        await WebSocketMessaging.SendAsync(
            socket,
            new ClientMessage
            {
                Register = new Register
                {
                    Certificate = ByteString.CopyFrom(identity.Certificate.Export(X509ContentType.Cert)),
                    Signature = ByteString.CopyFrom(identity.Sign(challenge.RegisterChallenge.Nonce.Span)),
                },
            },
            Ct);

        // A refusal is a close frame rather than an answer, so this is where a throttled registration shows up.
        var answer = await WebSocketMessaging.ReceiveAsync(socket, ServerMessage.Parser, Ct);
        return answer?.BodyCase == ServerMessage.BodyOneofCase.Registered ? answer.Registered.DeviceId : null;
    }
}
