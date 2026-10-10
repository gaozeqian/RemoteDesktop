using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using RemoteDesktop.Signaling;

namespace RemoteDesktop.Server.Shared;

/// <summary>
/// The token a relay sends back to an address before it will bind it, and checks when that address presents it
/// again. It is what makes a bind prove it can receive where it says it is: a bind carrying somebody else's address
/// never sees the token, so that address never becomes a route. Without it, a ticket holder could aim the relay at
/// any address it liked and the packets would arrive from the relay instead of from them.
/// </summary>
/// <remarks>
/// Computed rather than stored, so a bind that is still on its way costs the relay nothing to remember and there is
/// no table for an attacker to fill. The time goes in through a coarse window, which keeps a token from living
/// forever without the relay holding a clock per client.
/// </remarks>
public static class RelayCookie
{
    /// <summary>Must be the size the wire format reserves for the token.</summary>
    public const int Size = UdpControlPacket.RelayBindCookieSize;

    private static readonly byte[] Context = "rd-relay-bind-cookie-v1"u8.ToArray();

    private static readonly long WindowSeconds = 30;

    public static byte[] Compute(ReadOnlySpan<byte> key, ReadOnlySpan<byte> ticket, byte role, IPEndPoint from, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(from);
        return Compute(key, ticket, role, from, Window(now)).AsSpan(0, Size).ToArray();
    }

    /// <summary>
    /// Whether this is a token issued for exactly this ticket, role and address, within the last window. The window
    /// before it is accepted as well, so a bind that began just before the boundary is not thrown away.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> cookie, ReadOnlySpan<byte> key, ReadOnlySpan<byte> ticket, byte role, IPEndPoint from, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(from);
        if (cookie.Length != Size)
        {
            return false;
        }

        var window = Window(now);
        return CryptographicOperations.FixedTimeEquals(cookie, Compute(key, ticket, role, from, window).AsSpan(0, Size))
            || CryptographicOperations.FixedTimeEquals(cookie, Compute(key, ticket, role, from, window - 1).AsSpan(0, Size));
    }

    private static long Window(DateTimeOffset now) => now.ToUnixTimeSeconds() / WindowSeconds;

    private static byte[] Compute(ReadOnlySpan<byte> key, ReadOnlySpan<byte> ticket, byte role, IPEndPoint from, long window)
    {
        var address = from.Address.GetAddressBytes();
        var input = new byte[Context.Length + ticket.Length + 1 + address.Length + sizeof(ushort) + sizeof(long)];
        var span = input.AsSpan();

        Context.CopyTo(span);
        span = span[Context.Length..];
        ticket.CopyTo(span);
        span = span[ticket.Length..];
        span[0] = role;
        span = span[1..];
        address.CopyTo(span);
        span = span[address.Length..];
        BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)from.Port);
        BinaryPrimitives.WriteInt64BigEndian(span[sizeof(ushort)..], window);

        return HMACSHA256.HashData(key, input);
    }
}
