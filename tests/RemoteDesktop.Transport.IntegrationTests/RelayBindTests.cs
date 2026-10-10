using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Server.Relay;
using RemoteDesktop.Server.Shared;
using RemoteDesktop.Signaling;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// What it takes to become a route on the relay: a ticket, and a token the relay sent to the address that claimed
/// it. The token is the part that stops a ticket holder from aiming the relay at an address that never asked for it
/// — packets would otherwise arrive at that address from the relay instead of from whoever sent them.
/// </summary>
public class RelayBindTests
{
    private static readonly byte[] Key = [.. Enumerable.Range(1, 32).Select(value => (byte)value)];

    [Fact]
    public async Task A_bind_without_the_token_gets_the_token_and_no_route()
    {
        await using var relay = await TestRelay.StartAsync();

        var answer = await relay.ExchangeAsync(UdpControlPacket.CreateRelayBind((byte)RelayRole.Controller, [], relay.Ticket));

        Assert.True(UdpControlPacket.TryParse(answer, out var type, out var body));
        Assert.Equal(UdpControlPacketType.RelayBindChallenge, type);
        Assert.Equal(UdpControlPacket.RelayBindCookieSize, body.Length);
        Assert.Equal(0, relay.Relay.ActivePairings);
    }

    [Fact]
    public async Task Both_roles_binding_with_their_own_token_pairs_them()
    {
        await using var relay = await TestRelay.StartAsync();

        Assert.True(await relay.BindAsync(RelayRole.Controller));
        Assert.Equal(0, relay.Relay.ActivePairings);

        Assert.True(await relay.BindAsync(RelayRole.Host));
        Assert.Equal(1, relay.Relay.ActivePairings);
    }

    [Fact]
    public async Task A_token_issued_for_another_address_is_not_accepted()
    {
        await using var relay = await TestRelay.StartAsync();

        // Somebody else's token: computed for an address that is not this one.
        var elsewhere = RelayCookie.Compute(Key, relay.Ticket, (byte)RelayRole.Controller, new IPEndPoint(IPAddress.Parse("10.1.2.3"), 4000), relay.Now);

        Assert.False(await relay.BindWithAsync(RelayRole.Controller, elsewhere));
        Assert.Equal(0, relay.Relay.ActivePairings);
    }

    [Fact]
    public async Task A_token_issued_for_the_other_role_is_not_accepted()
    {
        await using var relay = await TestRelay.StartAsync();

        var forHost = RelayCookie.Compute(Key, relay.Ticket, (byte)RelayRole.Host, relay.ClientEndPoint, relay.Now);

        Assert.False(await relay.BindWithAsync(RelayRole.Controller, forHost));
        Assert.Equal(0, relay.Relay.ActivePairings);
    }

    /// <summary>A relay of its own on loopback, with a ticket this test signed and a clock it controls.</summary>
    private sealed class TestRelay : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly UdpClient _client;

        private TestRelay(UdpRelay relay, UdpClient client, byte[] ticket, FakeTimeProvider time)
        {
            Relay = relay;
            _client = client;
            Ticket = ticket;
            Now = time.GetUtcNow();
            _loop = relay.RunAsync(_stop.Token);
        }

        public UdpRelay Relay { get; }

        public byte[] Ticket { get; }

        public DateTimeOffset Now { get; }

        public IPEndPoint ClientEndPoint => (IPEndPoint)_client.Client.LocalEndPoint!;

        public static Task<TestRelay> StartAsync()
        {
            var time = new FakeTimeProvider();
            var relay = new UdpRelay(new IPEndPoint(IPAddress.Loopback, 0), Key, TimeSpan.FromMinutes(2), time, NullLogger<UdpRelay>.Instance);
            var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var ticket = new RelayTicket(Guid.NewGuid(), time.GetUtcNow() + TimeSpan.FromMinutes(2)).Sign(Key);
            return Task.FromResult(new TestRelay(relay, client, ticket, time));
        }

        /// <summary>The whole two-step bind, the way a client does it: ask, then present what came back.</summary>
        public async Task<bool> BindAsync(RelayRole role)
        {
            var answer = await ExchangeAsync(UdpControlPacket.CreateRelayBind((byte)role, [], Ticket));
            if (!UdpControlPacket.TryParse(answer, out var type, out var body) || type != UdpControlPacketType.RelayBindChallenge)
            {
                return false;
            }

            return await BindWithAsync(role, body.ToArray());
        }

        /// <summary>The second step on its own, for the tests that present a token the relay did not issue.</summary>
        public async Task<bool> BindWithAsync(RelayRole role, byte[] cookie)
        {
            var answer = await ExchangeAsync(UdpControlPacket.CreateRelayBind((byte)role, cookie, Ticket), expectAnswer: false);
            return answer.Length > 0 &&
                UdpControlPacket.TryParse(answer, out var type, out _) &&
                type == UdpControlPacketType.RelayBindAck;
        }

        public async Task<byte[]> ExchangeAsync(byte[] request, bool expectAnswer = true)
        {
            await _client.SendAsync(request, Relay.LocalEndPoint, Ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(expectAnswer ? 5 : 1));
            try
            {
                var answer = await _client.ReceiveAsync(timeout.Token);
                return answer.Buffer;
            }
            catch (OperationCanceledException)
            {
                return [];
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _loop;
            _client.Dispose();
            Relay.Dispose();
            _stop.Dispose();
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}

/// <summary>The token itself, away from any socket: what it is bound to and how long it lives.</summary>
public class RelayCookieTests
{
    private static readonly byte[] Key = [.. Enumerable.Range(1, 32).Select(value => (byte)value)];

    private static readonly byte[] Ticket = new RelayTicket(Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddYears(100)).Sign(Key);

    private static readonly IPEndPoint Address = new(IPAddress.Parse("192.0.2.10"), 4000);

    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(20_000);

    [Fact]
    public void A_token_is_accepted_for_the_ticket_role_and_address_it_was_made_for()
    {
        var cookie = RelayCookie.Compute(Key, Ticket, 1, Address, Now);

        Assert.Equal(UdpControlPacket.RelayBindCookieSize, cookie.Length);
        Assert.True(RelayCookie.Verify(cookie, Key, Ticket, 1, Address, Now));
    }

    [Fact]
    public void A_token_is_bound_to_all_three()
    {
        var cookie = RelayCookie.Compute(Key, Ticket, 1, Address, Now);
        var otherTicket = new RelayTicket(Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddYears(100)).Sign(Key);
        var otherAddress = new IPEndPoint(IPAddress.Parse("192.0.2.11"), 4000);

        Assert.False(RelayCookie.Verify(cookie, Key, otherTicket, 1, Address, Now));
        Assert.False(RelayCookie.Verify(cookie, Key, Ticket, 2, Address, Now));
        Assert.False(RelayCookie.Verify(cookie, Key, Ticket, 1, otherAddress, Now));

        // Another port on the same address is another address: the token is what proves reachability there.
        Assert.False(RelayCookie.Verify(cookie, Key, Ticket, 1, new IPEndPoint(Address.Address, 4001), Now));
    }

    [Fact]
    public void A_token_from_the_window_that_just_ended_is_still_good_and_an_old_one_is_not()
    {
        var cookie = RelayCookie.Compute(Key, Ticket, 1, Address, Now);

        Assert.True(RelayCookie.Verify(cookie, Key, Ticket, 1, Address, Now.AddSeconds(29)));
        Assert.False(RelayCookie.Verify(cookie, Key, Ticket, 1, Address, Now.AddMinutes(10)));
    }
}
