using System.Buffers;
using System.Net.WebSockets;
using Google.Protobuf;

namespace RemoteDesktop.Signaling;

/// <summary>One protobuf envelope per binary WebSocket message, on both the client and the server.</summary>
public static class WebSocketMessaging
{
    public const int MaxMessageSize = 64 * 1024;

    public static Task SendAsync(WebSocket socket, IMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(message);

        return socket.SendAsync(message.ToByteArray(), WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
    }

    /// <summary>Receives the next message, or <c>null</c> when the peer closed the socket.</summary>
    /// <exception cref="InvalidDataException">The message is not binary, too large or not a valid envelope.</exception>
    public static async Task<T?> ReceiveAsync<T>(WebSocket socket, MessageParser<T> parser, CancellationToken cancellationToken)
        where T : class, IMessage<T>
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(parser);

        var buffer = ArrayPool<byte>.Shared.Rent(MaxMessageSize);
        try
        {
            var length = 0;
            while (true)
            {
                if (length == MaxMessageSize)
                {
                    throw new InvalidDataException($"Signaling message exceeds {MaxMessageSize} bytes.");
                }

                var result = await socket.ReceiveAsync(buffer.AsMemory(length, MaxMessageSize - length), cancellationToken)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    throw new InvalidDataException("Signaling messages must be binary.");
                }

                length += result.Count;
                if (result.EndOfMessage)
                {
                    break;
                }
            }

            try
            {
                return parser.ParseFrom(buffer, 0, length);
            }
            catch (InvalidProtocolBufferException ex)
            {
                throw new InvalidDataException("Malformed signaling message.", ex);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
