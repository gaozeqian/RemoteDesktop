using System.Net;
using System.Security.Cryptography;
using RemoteDesktop.Server.Shared;
using RemoteDesktop.Signaling;

namespace RemoteDesktop.Core.UnitTests;

public class RelayTicketTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Signed_ticket_validates_until_it_expires()
    {
        var ticket = new RelayTicket(Guid.NewGuid(), Now.AddMinutes(2));
        var bytes = ticket.Sign(Key);

        Assert.Equal(ticket.SessionId, RelayTicket.Validate(bytes, Key, Now)!.SessionId);
        Assert.Null(RelayTicket.Validate(bytes, Key, Now.AddMinutes(3)));
    }

    [Fact]
    public void Tampered_ticket_or_wrong_key_is_rejected()
    {
        var bytes = new RelayTicket(Guid.NewGuid(), Now.AddMinutes(2)).Sign(Key);
        var tampered = (byte[])bytes.Clone();
        tampered[5] ^= 1;

        Assert.Null(RelayTicket.Validate(tampered, Key, Now));
        Assert.Null(RelayTicket.Validate(bytes, RandomNumberGenerator.GetBytes(32), Now));
        Assert.Null(RelayTicket.Validate(bytes.AsSpan(1), Key, Now));
    }
}

public class UdpControlPacketTests
{
    [Fact]
    public void Reflect_response_round_trips()
    {
        var transactionId = RandomNumberGenerator.GetBytes(UdpControlPacket.TransactionIdSize);
        var observed = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 61000);

        var packet = UdpControlPacket.CreateReflectResponse(transactionId, observed);

        Assert.True(UdpControlPacket.TryParse(packet, out var type, out var body));
        Assert.Equal(UdpControlPacketType.ReflectResponse, type);
        Assert.True(UdpControlPacket.TryParseReflectResponse(body, out var id, out var endPoint));
        Assert.Equal(transactionId, id.ToArray());
        Assert.Equal(observed, endPoint);
    }

    [Theory]
    [InlineData(new byte[] { 0xC0, (byte)'R', (byte)'D', 1 })] // QUIC long header
    [InlineData(new byte[] { 0x40, (byte)'R', (byte)'D', 1 })] // QUIC short header
    [InlineData(new byte[] { 0x00, (byte)'R', (byte)'D', 99 })] // unknown type
    [InlineData(new byte[] { 0x00, (byte)'R' })]
    public void QUIC_and_garbage_are_not_control_packets(byte[] packet) =>
        Assert.False(UdpControlPacket.TryParse(packet, out _, out _));
}
