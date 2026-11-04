using System.Buffers.Binary;

namespace RemoteDesktop.Protocol.Framing;

/// <summary>
/// Wire format of every message on a channel stream: <c>[uint32 payload length][uint16 message type][payload]</c>,
/// big-endian. Control-style channels carry protobuf envelopes (<see cref="ProtobufFrames"/>); video, audio, file
/// chunks and terminal data are raw bytes.
/// </summary>
public static class FrameCodec
{
    public const int HeaderSize = sizeof(uint) + sizeof(ushort);

    /// <summary>Upper bound that protects against a malicious or corrupt length prefix.</summary>
    public const int DefaultMaxPayloadSize = 16 * 1024 * 1024;

    public static async ValueTask WriteAsync(Stream stream, Frame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)frame.Payload.Length);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(sizeof(uint)), frame.MessageType);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (!frame.Payload.IsEmpty)
        {
            await stream.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the next frame, or returns <c>null</c> when the stream ended cleanly between frames.</summary>
    /// <exception cref="InvalidDataException">The stream ended mid-frame or the length exceeds <paramref name="maxPayloadSize"/>.</exception>
    public static async ValueTask<Frame?> ReadAsync(
        Stream stream,
        int maxPayloadSize = DefaultMaxPayloadSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[HeaderSize];
        var read = await stream.ReadAtLeastAsync(header, HeaderSize, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < HeaderSize)
        {
            throw new InvalidDataException("Stream ended inside a frame header.");
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        var messageType = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(sizeof(uint)));
        if (length > (uint)maxPayloadSize)
        {
            throw new InvalidDataException($"Frame payload of {length} bytes exceeds the limit of {maxPayloadSize}.");
        }

        var payload = new byte[length];
        if (length > 0)
        {
            read = await stream.ReadAtLeastAsync(payload, (int)length, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);
            if (read < length)
            {
                throw new InvalidDataException("Stream ended inside a frame payload.");
            }
        }

        return new Frame(messageType, payload);
    }
}
