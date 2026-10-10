using System.Buffers.Binary;
using System.Net;

namespace RemoteDesktop.Signaling;

public enum UdpControlPacketType : byte
{
    /// <summary>Client asks the reflector which public endpoint it sees.</summary>
    ReflectRequest = 1,

    /// <summary>Reflector answers with the observed endpoint.</summary>
    ReflectResponse = 2,

    /// <summary>Client binds its UDP endpoint to a relay ticket.</summary>
    RelayBind = 3,

    /// <summary>Relay confirms the bind.</summary>
    RelayBindAck = 4,

    /// <summary>Opens the NAT mapping towards a peer; the content is ignored.</summary>
    Punch = 5,

    /// <summary>
    /// Relay answers a bind that carried no token with a token, and the client binds again with it. Receiving the
    /// token is what proves the bind really comes from the address it claims — the point of the exchange.
    /// </summary>
    RelayBindChallenge = 6,
}

/// <summary>
/// Small UDP packets used for address reflection, relay binding and hole punching. They start with a zero byte,
/// which is never a valid first byte of a QUIC packet (the QUIC fixed bit is always set), so a relay or listener
/// can tell them apart from QUIC traffic on the same port.
/// </summary>
public static class UdpControlPacket
{
    private const byte Marker = 0x00;
    private const byte Magic1 = (byte)'R';
    private const byte Magic2 = (byte)'D';
    private const int HeaderSize = 4;

    public const int TransactionIdSize = 12;

    /// <summary>Size of the token in a <see cref="UdpControlPacketType.RelayBindChallenge"/> and of the field it
    /// occupies in a bind. See <c>RelayCookie</c> for what it is made of.</summary>
    public const int RelayBindCookieSize = 16;

    /// <summary>Body of a bind: <c>[role u8][token 16][ticket]</c>, the token zero-filled on the first attempt.</summary>
    public static byte[] CreateRelayBind(byte role, ReadOnlySpan<byte> cookie, ReadOnlySpan<byte> ticket)
    {
        var body = new byte[1 + RelayBindCookieSize + ticket.Length];
        body[0] = role;
        cookie[..Math.Min(cookie.Length, RelayBindCookieSize)].CopyTo(body.AsSpan(1));
        ticket.CopyTo(body.AsSpan(1 + RelayBindCookieSize));
        return Create(UdpControlPacketType.RelayBind, body);
    }

    public static bool TryParseRelayBind(ReadOnlySpan<byte> body, out byte role, out ReadOnlySpan<byte> cookie, out ReadOnlySpan<byte> ticket)
    {
        role = 0;
        cookie = default;
        ticket = default;
        if (body.Length < 1 + RelayBindCookieSize)
        {
            return false;
        }

        role = body[0];
        cookie = body.Slice(1, RelayBindCookieSize);
        ticket = body[(1 + RelayBindCookieSize)..];
        return true;
    }

    public static bool TryParse(ReadOnlySpan<byte> packet, out UdpControlPacketType type, out ReadOnlySpan<byte> body)
    {
        type = default;
        body = default;
        if (packet.Length < HeaderSize || packet[0] != Marker || packet[1] != Magic1 || packet[2] != Magic2 ||
            !Enum.IsDefined((UdpControlPacketType)packet[3]))
        {
            return false;
        }

        type = (UdpControlPacketType)packet[3];
        body = packet[HeaderSize..];
        return true;
    }

    public static byte[] Create(UdpControlPacketType type, ReadOnlySpan<byte> body)
    {
        var packet = new byte[HeaderSize + body.Length];
        packet[0] = Marker;
        packet[1] = Magic1;
        packet[2] = Magic2;
        packet[3] = (byte)type;
        body.CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }

    /// <summary>Body: transaction id followed by the endpoint as [port u16][address bytes].</summary>
    public static byte[] CreateReflectResponse(ReadOnlySpan<byte> transactionId, IPEndPoint observed)
    {
        ArgumentNullException.ThrowIfNull(observed);

        var address = observed.Address.GetAddressBytes();
        var body = new byte[TransactionIdSize + sizeof(ushort) + address.Length];
        transactionId[..TransactionIdSize].CopyTo(body);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(TransactionIdSize), (ushort)observed.Port);
        address.CopyTo(body.AsSpan(TransactionIdSize + sizeof(ushort)));
        return Create(UdpControlPacketType.ReflectResponse, body);
    }

    public static bool TryParseReflectResponse(ReadOnlySpan<byte> body, out ReadOnlySpan<byte> transactionId, out IPEndPoint? observed)
    {
        transactionId = default;
        observed = null;
        var addressLength = body.Length - TransactionIdSize - sizeof(ushort);
        if (addressLength is not (4 or 16))
        {
            return false;
        }

        transactionId = body[..TransactionIdSize];
        var port = BinaryPrimitives.ReadUInt16BigEndian(body[TransactionIdSize..]);
        observed = new IPEndPoint(new IPAddress(body[(TransactionIdSize + sizeof(ushort))..]), port);
        return true;
    }
}
