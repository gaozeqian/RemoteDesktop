using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RemoteDesktop.Server.Shared;

/// <summary>
/// Issued by the rendezvous server for one connection attempt; both peers present it to the relay, which pairs them
/// and forwards their (end-to-end encrypted) UDP packets. Signed with a key shared by rendezvous and relay, so the
/// relay validates it without any shared state.
/// </summary>
/// <remarks>Wire format: <c>[version u8][session id 16][expires unix-seconds i64][HMAC-SHA256 32]</c>.</remarks>
public sealed record RelayTicket(Guid SessionId, DateTimeOffset ExpiresAt)
{
    private const byte Version = 1;
    private const int PayloadSize = 1 + 16 + sizeof(long);
    private const int MacSize = 32;

    public const int Size = PayloadSize + MacSize;

    public byte[] Sign(ReadOnlySpan<byte> key)
    {
        var ticket = new byte[Size];
        ticket[0] = Version;
        SessionId.TryWriteBytes(ticket.AsSpan(1, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(ticket.AsSpan(17), ExpiresAt.ToUnixTimeSeconds());
        HMACSHA256.HashData(key, ticket.AsSpan(0, PayloadSize), ticket.AsSpan(PayloadSize));
        return ticket;
    }

    /// <summary>Returns the ticket when the signature is valid and it has not expired.</summary>
    public static RelayTicket? Validate(ReadOnlySpan<byte> ticket, ReadOnlySpan<byte> key, DateTimeOffset now)
    {
        if (ticket.Length != Size || ticket[0] != Version)
        {
            return null;
        }

        Span<byte> expected = stackalloc byte[MacSize];
        HMACSHA256.HashData(key, ticket[..PayloadSize], expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, ticket[PayloadSize..]))
        {
            return null;
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64BigEndian(ticket[17..]));
        return expiresAt <= now ? null : new RelayTicket(new Guid(ticket.Slice(1, 16), bigEndian: true), expiresAt);
    }
}
