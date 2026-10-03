using Google.Protobuf;

namespace RemoteDesktop.Protocol.Framing;

/// <summary>Sends and receives protobuf envelopes as frames. Each channel uses a single envelope type.</summary>
public static class ProtobufFrames
{
    public const ushort EnvelopeMessageType = 1;

    public static ValueTask WriteAsync(Stream stream, IMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return FrameCodec.WriteAsync(stream, new Frame(EnvelopeMessageType, message.ToByteArray()), cancellationToken);
    }

    /// <summary>Reads the next envelope, or <c>null</c> when the stream ended cleanly.</summary>
    /// <exception cref="InvalidDataException">The frame is malformed or not an envelope.</exception>
    public static async ValueTask<T?> ReadAsync<T>(
        Stream stream,
        MessageParser<T> parser,
        int maxPayloadSize = FrameCodec.DefaultMaxPayloadSize,
        CancellationToken cancellationToken = default)
        where T : class, IMessage<T>
    {
        ArgumentNullException.ThrowIfNull(parser);

        var frame = await FrameCodec.ReadAsync(stream, maxPayloadSize, cancellationToken).ConfigureAwait(false);
        if (frame is null)
        {
            return null;
        }

        if (frame.Value.MessageType != EnvelopeMessageType)
        {
            throw new InvalidDataException($"Unexpected message type {frame.Value.MessageType}.");
        }

        try
        {
            return parser.ParseFrom(frame.Value.Payload.Span);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new InvalidDataException("Malformed protobuf message.", ex);
        }
    }
}
