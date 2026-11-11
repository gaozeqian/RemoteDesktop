using System.Net;
using System.Net.Sockets;
using RemoteDesktop.Signaling;
using RemoteDesktop.Transport.Nat;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The plain UDP socket that opens NAT mappings and talks to the reflector and the relay before QUIC takes the port,
/// driven against fake peers on loopback.
/// </summary>
public sealed class PunchSocketTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] PunchPacket = [0x00, (byte)'R', (byte)'D', (byte)UdpControlPacketType.Punch];

    [Fact]
    public async Task Punching_sends_three_identical_punch_packets_to_every_target()
    {
        using var first = new FakeUdpPeer();
        using var second = new FakeUdpPeer();
        using var punch = PunchSocket.Bind();

        await punch.PunchAsync([first.EndPoint, second.EndPoint], Ct);

        foreach (var peer in new[] { first, second })
        {
            var packets = await peer.WaitForAsync(3);
            Assert.All(packets, packet => Assert.Equal(PunchPacket, packet.Data));

            // Every packet comes from the port QUIC is about to inherit; that is what opens the NAT mapping.
            Assert.All(packets, packet => Assert.Equal(punch.Port, packet.From.Port));
            Assert.Empty(await peer.DrainAsync());
        }
    }

    [Fact]
    public async Task The_punch_packet_is_a_four_byte_control_header_with_no_body_and_a_leading_zero_byte()
    {
        using var peer = new FakeUdpPeer();
        using var punch = PunchSocket.Bind();

        await punch.PunchAsync([peer.EndPoint], Ct);

        var packet = (await peer.WaitForAsync(3))[0].Data;
        Assert.Equal(4, packet.Length);

        // Zero is never the first byte of a QUIC packet, which is how the relay and the listener tell them apart.
        Assert.Equal(0, packet[0]);
        Assert.True(UdpControlPacket.TryParse(packet, out var type, out var body));
        Assert.Equal(UdpControlPacketType.Punch, type);
        Assert.True(body.IsEmpty);
    }

    [Fact]
    public async Task An_empty_target_list_sends_nothing_and_completes()
    {
        using var bystander = new FakeUdpPeer();
        using var punch = PunchSocket.Bind();

        await punch.PunchAsync([], Ct);

        Assert.Empty(await bystander.DrainAsync());
    }

    [Fact]
    public async Task The_target_sequence_is_enumerated_once_not_once_per_round()
    {
        using var peer = new FakeUdpPeer();
        using var punch = PunchSocket.Bind();
        var enumerations = 0;

        IEnumerable<IPEndPoint> Targets()
        {
            enumerations++;
            yield return peer.EndPoint;
        }

        await punch.PunchAsync(Targets(), Ct);

        Assert.Equal(1, enumerations);
        Assert.Equal(3, (await peer.WaitForAsync(3)).Count);
    }

    [Fact]
    public async Task A_target_that_cannot_be_sent_to_does_not_stop_the_others()
    {
        using var reachable = new FakeUdpPeer();
        using var punch = PunchSocket.Bind();

        // Port 0 is not a destination anywhere: the send fails with a socket error, as an interface without a route
        // would. The candidate list comes from the peer, so an unusable entry is expected, not exceptional.
        await punch.PunchAsync([new IPEndPoint(IPAddress.Loopback, 0), reachable.EndPoint], Ct);

        Assert.Equal(3, (await reachable.WaitForAsync(3)).Count);
    }

    [Fact]
    public async Task Punching_with_a_cancelled_token_throws_instead_of_pretending_to_have_punched()
    {
        using var peer = new FakeUdpPeer();
        using var punch = PunchSocket.Bind();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => punch.PunchAsync([peer.EndPoint], cancelled.Token));
        Assert.Empty(await peer.DrainAsync());
    }

    [Fact]
    public async Task Reflecting_returns_the_endpoint_the_reflector_reports_for_our_transaction()
    {
        using var reflector = new FakeUdpPeer();
        var observed = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 41234);
        reflector.OnPacket = (packet, from) =>
        {
            if (UdpControlPacket.TryParse(packet, out var type, out var body) && type == UdpControlPacketType.ReflectRequest)
            {
                reflector.Send(UdpControlPacket.CreateReflectResponse(body[..UdpControlPacket.TransactionIdSize], observed), from);
            }
        };
        using var punch = PunchSocket.Bind();

        var result = await punch.ReflectAsync(reflector.EndPoint, TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(observed, result);
    }

    [Fact]
    public async Task A_response_for_another_transaction_is_ignored_and_the_wait_ends_in_null()
    {
        using var reflector = new FakeUdpPeer();
        var observed = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 41234);
        reflector.OnPacket = (_, from) =>
            reflector.Send(UdpControlPacket.CreateReflectResponse(new byte[UdpControlPacket.TransactionIdSize], observed), from);
        using var punch = PunchSocket.Bind();

        var result = await punch.ReflectAsync(reflector.EndPoint, TimeSpan.FromMilliseconds(400), Ct);

        Assert.Null(result);
    }

    [Fact]
    public async Task A_response_from_an_address_other_than_the_reflector_is_ignored()
    {
        using var reflector = new FakeUdpPeer();
        using var impostor = new FakeUdpPeer();
        var observed = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 41234);
        reflector.OnPacket = (packet, from) =>
        {
            if (UdpControlPacket.TryParse(packet, out _, out var body))
            {
                // The right transaction id, from the wrong socket.
                impostor.Send(UdpControlPacket.CreateReflectResponse(body[..UdpControlPacket.TransactionIdSize], observed), from);
            }
        };
        using var punch = PunchSocket.Bind();

        var result = await punch.ReflectAsync(reflector.EndPoint, TimeSpan.FromMilliseconds(400), Ct);

        Assert.Null(result);
    }

    [Fact]
    public async Task A_silent_reflector_gives_null_after_the_timeout_rather_than_an_exception()
    {
        using var reflector = new FakeUdpPeer();
        using var punch = PunchSocket.Bind();

        var result = await punch.ReflectAsync(reflector.EndPoint, TimeSpan.FromMilliseconds(300), Ct);

        Assert.Null(result);
    }

    [Fact]
    public async Task An_unanswered_request_is_sent_again_within_the_timeout()
    {
        using var reflector = new FakeUdpPeer();
        var observed = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 41234);
        var requests = 0;
        reflector.OnPacket = (packet, from) =>
        {
            // The first datagram is "lost"; only the retransmission is answered.
            if (Interlocked.Increment(ref requests) >= 2 && UdpControlPacket.TryParse(packet, out _, out var body))
            {
                reflector.Send(UdpControlPacket.CreateReflectResponse(body[..UdpControlPacket.TransactionIdSize], observed), from);
            }
        };
        using var punch = PunchSocket.Bind();

        var result = await punch.ReflectAsync(reflector.EndPoint, TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(observed, result);
        Assert.True(Volatile.Read(ref requests) >= 2);
    }

    [Fact]
    public async Task Cancelling_a_reflection_throws_while_a_timeout_does_not()
    {
        using var reflector = new FakeUdpPeer();
        using var punch = PunchSocket.Bind();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => punch.ReflectAsync(reflector.EndPoint, TimeSpan.FromSeconds(30), cancel.Token));
    }

    [Fact]
    public async Task Binding_the_relay_answers_the_challenge_with_the_token_the_role_and_the_ticket()
    {
        using var relay = new FakeUdpPeer();
        var cookie = Enumerable.Range(1, UdpControlPacket.RelayBindCookieSize).Select(i => (byte)i).ToArray();
        var ticket = new byte[] { 9, 8, 7, 6, 5 };
        var binds = new List<(byte Role, byte[] Cookie, byte[] Ticket)>();
        relay.OnPacket = (packet, from) =>
        {
            if (!UdpControlPacket.TryParse(packet, out var type, out var body) || type != UdpControlPacketType.RelayBind ||
                !UdpControlPacket.TryParseRelayBind(body, out var role, out var received, out var receivedTicket))
            {
                return;
            }

            lock (binds)
            {
                binds.Add((role, received.ToArray(), receivedTicket.ToArray()));
            }

            relay.Send(
                received.ToArray().All(b => b == 0)
                    ? UdpControlPacket.Create(UdpControlPacketType.RelayBindChallenge, cookie)
                    : UdpControlPacket.Create(UdpControlPacketType.RelayBindAck, []),
                from);
        };
        using var punch = PunchSocket.Bind();

        var bound = await punch.BindRelayAsync(relay.EndPoint, ticket, RelayRole.Controller, TimeSpan.FromSeconds(5), Ct);

        Assert.True(bound);
        lock (binds)
        {
            // A bind is retransmitted until it is answered, so the same step can legitimately appear twice: under a
            // fully parallel solution run the timer fired while the answer was still in flight, which is how this
            // assertion was found to be testing the scheduler rather than the protocol. What the two steps have to
            // be is asserted below; how many datagrams carried them is not something the product promises.
            Assert.True(binds.Count >= 2, $"expected both binds, saw {binds.Count}");

            // The very first packet is the one with no token: it is what asks for the challenge.
            Assert.All(binds[0].Cookie, b => Assert.Equal(0, b));

            // After that, every packet is one of exactly two shapes — a repeated step one or a step two carrying the
            // cookie that came back. Nothing else may be sent.
            var tokenless = binds.Where(bind => bind.Cookie.All(b => b == 0)).ToList();
            var withToken = binds.Where(bind => bind.Cookie.SequenceEqual(cookie)).ToList();
            Assert.NotEmpty(tokenless);
            Assert.NotEmpty(withToken);
            Assert.Equal(binds.Count, tokenless.Count + withToken.Count);

            Assert.All(binds, bind => Assert.Equal((byte)RelayRole.Controller, bind.Role));
            Assert.All(binds, bind => Assert.Equal(ticket, bind.Ticket));
        }
    }

    [Fact]
    public async Task A_challenge_of_the_wrong_size_fails_the_bind_without_waiting_for_the_timeout()
    {
        using var relay = new FakeUdpPeer();
        var requests = 0;
        relay.OnPacket = (_, from) =>
        {
            Interlocked.Increment(ref requests);
            relay.Send(UdpControlPacket.Create(UdpControlPacketType.RelayBindChallenge, new byte[UdpControlPacket.RelayBindCookieSize - 1]), from);
        };
        using var punch = PunchSocket.Bind();

        var bound = await punch.BindRelayAsync(relay.EndPoint, [1], RelayRole.Host, TimeSpan.FromSeconds(30), Ct);

        Assert.False(bound);

        // One attempt and one retry, each ending as soon as the malformed token arrives.
        Assert.Equal(2, Volatile.Read(ref requests));
    }

    [Fact]
    public async Task A_bind_that_fails_once_is_tried_a_second_time_before_giving_up()
    {
        using var relay = new FakeUdpPeer();
        var cookie = Enumerable.Repeat((byte)0xAB, UdpControlPacket.RelayBindCookieSize).ToArray();
        var challenges = 0;
        relay.OnPacket = (packet, from) =>
        {
            if (!UdpControlPacket.TryParse(packet, out _, out var body) ||
                !UdpControlPacket.TryParseRelayBind(body, out _, out var received, out _))
            {
                return;
            }

            if (received.ToArray().All(b => b == 0))
            {
                Interlocked.Increment(ref challenges);
                relay.Send(UdpControlPacket.Create(UdpControlPacketType.RelayBindChallenge, cookie), from);
            }
            else if (Volatile.Read(ref challenges) >= 2)
            {
                // The acknowledgement of the first attempt never comes; the second attempt's does.
                relay.Send(UdpControlPacket.Create(UdpControlPacketType.RelayBindAck, []), from);
            }
        };
        using var punch = PunchSocket.Bind();

        var bound = await punch.BindRelayAsync(relay.EndPoint, [1, 2], RelayRole.Host, TimeSpan.FromMilliseconds(400), Ct);

        Assert.True(bound);
        Assert.True(Volatile.Read(ref challenges) >= 2);
    }

    [Fact]
    public async Task A_silent_relay_fails_the_bind()
    {
        using var relay = new FakeUdpPeer();
        using var punch = PunchSocket.Bind();

        var bound = await punch.BindRelayAsync(relay.EndPoint, [1], RelayRole.Host, TimeSpan.FromMilliseconds(150), Ct);

        Assert.False(bound);
    }

    [Fact]
    public void Binding_port_zero_picks_a_free_port_and_binding_a_taken_one_fails()
    {
        using var first = PunchSocket.Bind();
        Assert.InRange(first.Port, 1, ushort.MaxValue);

        Assert.Throws<SocketException>(() => PunchSocket.Bind(first.Port));
    }

    private sealed record Datagram(byte[] Data, IPEndPoint From);

    /// <summary>A UDP socket on loopback that records what arrives and can answer it.</summary>
    private sealed class FakeUdpPeer : IDisposable
    {
        private static readonly TimeSpan QuietWindow = TimeSpan.FromMilliseconds(100);

        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Datagram> _received = [];
        private readonly Task _loop;

        public FakeUdpPeer()
        {
            if (OperatingSystem.IsWindows())
            {
                _socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
            }

            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _loop = Task.Run(ReceiveLoopAsync);
        }

        public IPEndPoint EndPoint => (IPEndPoint)_socket.LocalEndPoint!;

        /// <summary>Called for every datagram, after it is recorded.</summary>
        public Action<byte[], IPEndPoint>? OnPacket { get; set; }

        public void Send(byte[] data, IPEndPoint to) => _socket.SendTo(data, to);

        /// <summary>Waits until at least <paramref name="count"/> datagrams have arrived and returns everything taken so far.</summary>
        public async Task<List<Datagram>> WaitForAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                lock (_received)
                {
                    if (_received.Count >= count)
                    {
                        var all = _received.ToList();
                        _received.Clear();
                        return all;
                    }
                }

                await Task.Delay(10, timeout.Token);
            }
        }

        /// <summary>
        /// What has arrived so far, after a quiet window for the receive loop to catch up with the sender. A
        /// "nothing else was sent" assertion needs that window; awaiting it keeps the test thread free.
        /// </summary>
        public async Task<List<Datagram>> DrainAsync()
        {
            await Task.Delay(QuietWindow, Ct);
            lock (_received)
            {
                var all = _received.ToList();
                _received.Clear();
                return all;
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _socket.Dispose();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // The loop ends with the socket.
            }

            _stop.Dispose();
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[1500];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (!_stop.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                var data = buffer.AsSpan(0, result.ReceivedBytes).ToArray();
                var from = (IPEndPoint)result.RemoteEndPoint;
                lock (_received)
                {
                    _received.Add(new Datagram(data, from));
                }

                OnPacket?.Invoke(data, from);
            }
        }
    }
}
