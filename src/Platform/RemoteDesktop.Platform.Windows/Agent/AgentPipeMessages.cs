using System.Buffers.Binary;
using RemoteDesktop.Abstractions.Agent;

namespace RemoteDesktop.Platform.Windows.Agent;

internal enum AgentPipeRole : byte
{
    /// <summary>The single connection that carries requests and notifications.</summary>
    Control = 0,

    /// <summary>A connection that becomes the byte stream of one feature channel.</summary>
    Data = 1,
}

internal enum AgentPipeMessage : byte
{
    /// <summary>Service → agent, answer to the hello; payload is one byte, 1 = accepted.</summary>
    HelloAck = 1,

    /// <summary>Service → agent: connect a data pipe for a channel. Payload: channel id (16), kind (1).</summary>
    OpenChannel = 2,

    /// <summary>Service → agent: exit.</summary>
    Shutdown = 3,

    /// <summary>Agent → service: the input desktop changed. Payload: <see cref="DesktopKind"/> (1).</summary>
    DesktopChanged = 4,

    /// <summary>Agent → service: raise Ctrl+Alt+Del.</summary>
    SecureAttention = 5,
}

internal readonly record struct AgentHello(AgentPipeRole Role, int ProcessId, Guid ChannelId);

/// <summary>
/// Wire format of the service ↔ agent pipes. Hello (agent → service, first thing on every connection):
/// <c>[magic u32][version u16][process id u32][role u8][channel id 16]</c>. Afterwards the control pipe carries
/// <c>[type u8][length u16][payload]</c> messages in both directions, and a data pipe is the raw channel stream.
/// </summary>
internal static class AgentPipeMessages
{
    public const int HelloSize = 4 + 2 + 4 + 1 + 16;

    private const uint Magic = 0x52444147; // "RDAG"
    private const ushort Version = 1;
    private const int MaxPayload = 256;

    public static async ValueTask WriteHelloAsync(Stream stream, AgentPipeRole role, int processId, Guid channelId, CancellationToken cancellationToken)
    {
        var hello = new byte[HelloSize];
        BinaryPrimitives.WriteUInt32BigEndian(hello, Magic);
        BinaryPrimitives.WriteUInt16BigEndian(hello.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt32BigEndian(hello.AsSpan(6), (uint)processId);
        hello[10] = (byte)role;
        channelId.TryWriteBytes(hello.AsSpan(11), bigEndian: true, out _);
        await stream.WriteAsync(hello, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <returns>The hello, or <c>null</c> when the peer does not speak this protocol.</returns>
    public static async ValueTask<AgentHello?> ReadHelloAsync(Stream stream, CancellationToken cancellationToken)
    {
        var hello = new byte[HelloSize];
        await stream.ReadExactlyAsync(hello, cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32BigEndian(hello) != Magic ||
            BinaryPrimitives.ReadUInt16BigEndian(hello.AsSpan(4)) != Version ||
            !Enum.IsDefined((AgentPipeRole)hello[10]))
        {
            return null;
        }

        return new AgentHello(
            (AgentPipeRole)hello[10],
            (int)BinaryPrimitives.ReadUInt32BigEndian(hello.AsSpan(6)),
            new Guid(hello.AsSpan(11, 16), bigEndian: true));
    }

    public static async ValueTask WriteMessageAsync(Stream stream, AgentPipeMessage type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length > MaxPayload)
        {
            throw new ArgumentException("Agent message too large.", nameof(payload));
        }

        var message = new byte[3 + payload.Length];
        message[0] = (byte)type;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(1), (ushort)payload.Length);
        payload.CopyTo(message.AsMemory(3));
        await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <returns>The next message, or <c>null</c> when the stream ended between messages.</returns>
    /// <exception cref="InvalidDataException">Unknown type, oversized payload or a message cut short.</exception>
    public static async ValueTask<(AgentPipeMessage Type, byte[] Payload)?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[3];
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < header.Length || !Enum.IsDefined((AgentPipeMessage)header[0]))
        {
            throw new InvalidDataException("Malformed agent message.");
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1));
        if (length > MaxPayload)
        {
            throw new InvalidDataException("Agent message too large.");
        }

        var payload = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("Agent message cut short.", ex);
        }

        return ((AgentPipeMessage)header[0], payload);
    }

    public static byte[] OpenChannelPayload(Guid channelId, AgentChannelKind kind)
    {
        var payload = new byte[17];
        channelId.TryWriteBytes(payload, bigEndian: true, out _);
        payload[16] = (byte)kind;
        return payload;
    }
}
