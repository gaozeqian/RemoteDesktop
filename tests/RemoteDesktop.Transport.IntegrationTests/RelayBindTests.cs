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
    public async Task A_pairing_that_goes_idle_is_reaped_without_another_packet()
    {
        await using var relay = await TestRelay.StartAsync(cleanupInterval: TimeSpan.FromMilliseconds(100));

        Assert.True(await relay.BindAsync(RelayRole.Controller));
        Assert.True(await relay.BindAsync(RelayRole.Host));
        Assert.Equal(1, relay.Relay.ActivePairings);

        // Both peers now go silent. The sweep used to run only after an incoming packet, so nothing would ever
        // remove this pairing; the relay's own timer has to do it.
        relay.Advance(TimeSpan.FromMinutes(3));

        Assert.True(
            await WaitUntilAsync(() => relay.Relay.ActivePairings == 0, TimeSpan.FromSeconds(5)),
            "an idle pairing must be swept on the relay's timer, not on the next packet");
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

    [Fact]
    public async Task Invalid_tickets_are_summarized_instead_of_logging_every_packet()
    {
        var logger = new CountingLogger<UdpRelay>();
        await using var relay = await TestRelay.StartAsync(logger: logger);
        var invalid = new RelayTicket(Guid.NewGuid(), relay.Now.AddMinutes(2)).Sign(Enumerable.Repeat((byte)0x5a, 32).ToArray());
        var packet = UdpControlPacket.CreateRelayBind((byte)RelayRole.Controller, [], invalid);

        for (var i = 0; i < 20; i++)
        {
            await relay.SendAsync(packet);
        }

        Assert.True(await WaitUntilAsync(() => logger.Warnings >= 1, TimeSpan.FromSeconds(5)), "one summary is written");
        Assert.Equal(1, logger.Warnings);

        relay.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        await relay.SendAsync(packet);

        Assert.True(await WaitUntilAsync(() => logger.Warnings == 2, TimeSpan.FromSeconds(5)), "the next window writes another summary");
    }

    [Fact]
    public async Task Tokens_that_do_not_match_are_summarized_too_not_logged_per_packet()
    {
        // Anyone with a valid ticket (every registered device can ask for one) can repeat a wrong token for free.
        var logger = new CountingLogger<UdpRelay>();
        await using var relay = await TestRelay.StartAsync(logger: logger);
        var elsewhere = RelayCookie.Compute(Key, relay.Ticket, (byte)RelayRole.Controller, new IPEndPoint(IPAddress.Parse("10.1.2.3"), 4000), relay.Now);
        var packet = UdpControlPacket.CreateRelayBind((byte)RelayRole.Controller, elsewhere, relay.Ticket);

        for (var i = 0; i < 20; i++)
        {
            await relay.SendAsync(packet);
        }

        Assert.True(await WaitUntilAsync(() => logger.Warnings >= 1, TimeSpan.FromSeconds(5)), "one summary is written");

        // Let the relay get through the rest of the burst, then check that none of it produced a line of its own.
        await Task.Delay(TimeSpan.FromMilliseconds(300), Ct);
        Assert.Equal(1, logger.Warnings);

        relay.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        await relay.SendAsync(packet);

        Assert.True(await WaitUntilAsync(() => logger.Warnings == 2, TimeSpan.FromSeconds(5)), "the next window writes another summary");
    }

    /// <summary>
    /// The receive loop is the whole relay. An exception out of packet handling (here, the console's event sink
    /// failing while a pairing is announced) used to end it, and the host's default is to stop the process.
    /// </summary>
    [Fact]
    public async Task A_failure_while_handling_a_packet_does_not_stop_the_relay()
    {
        var sink = new ThrowingEventSink();
        await using var relay = await TestRelay.StartAsync(events: sink);

        Assert.True(await relay.BindAsync(RelayRole.Controller));

        // The second bind completes the pairing, which publishes an event, which throws. No ack for that packet.
        Assert.False(await relay.BindAsync(RelayRole.Host));
        Assert.True(sink.Published > 0, "the failing path must have been reached");
        Assert.True(relay.Running, "one bad packet must not end the receive loop");

        // The same relay still answers, and the host's retransmitted bind is acknowledged as a retransmission.
        Assert.True(await relay.BindAsync(RelayRole.Host));
        Assert.Equal(1, relay.Relay.ActivePairings);
    }

    /// <summary>The idle sweep runs on the relay's own timer and publishes too; it gets the same protection.</summary>
    [Fact]
    public async Task A_failure_while_closing_an_idle_pairing_does_not_stop_the_relay()
    {
        var sink = new ThrowingEventSink();
        await using var relay = await TestRelay.StartAsync(cleanupInterval: TimeSpan.FromMilliseconds(100), events: sink);

        Assert.True(await relay.BindAsync(RelayRole.Controller));
        Assert.False(await relay.BindAsync(RelayRole.Host));
        var publishedBefore = sink.Published;

        relay.Advance(TimeSpan.FromMinutes(3));
        Assert.True(
            await WaitUntilAsync(() => relay.Relay.ActivePairings == 0 && sink.Published > publishedBefore, TimeSpan.FromSeconds(5)),
            "the sweep must have closed the pairing and tried to announce it");

        Assert.True(relay.Running, "a failing sweep must not end the receive loop");

        // The first ticket has expired by now; a fresh one proves the relay still answers.
        Assert.True(await relay.BindAsync(RelayRole.Controller, relay.FreshTicket()));
    }

    private sealed class ThrowingEventSink : IOpsEventSink
    {
        private int _published;

        public int Published => Volatile.Read(ref _published);

        public bool Enabled => true;

        public void Publish(OpsEventEnvelope envelope)
        {
            Interlocked.Increment(ref _published);
            throw new InvalidOperationException("the event queue is broken");
        }
    }

    /// <summary>A relay of its own on loopback, with a ticket this test signed and a clock it controls.</summary>
    private sealed class TestRelay : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly UdpClient _client;

        private readonly FakeTimeProvider _time;

        private TestRelay(UdpRelay relay, UdpClient client, byte[] ticket, FakeTimeProvider time)
        {
            Relay = relay;
            _client = client;
            Ticket = ticket;
            _time = time;
            Now = time.GetUtcNow();
            _loop = relay.RunAsync(_stop.Token);
        }

        public UdpRelay Relay { get; }

        /// <summary>Moves the clock the relay reads for activity timestamps.</summary>
        public void Advance(TimeSpan by) => _time.Advance(by);

        public byte[] Ticket { get; }

        /// <summary>A ticket for a new session that is valid at the relay's current time.</summary>
        public byte[] FreshTicket() => new RelayTicket(Guid.NewGuid(), _time.GetUtcNow() + TimeSpan.FromMinutes(2)).Sign(Key);

        public DateTimeOffset Now { get; }

        public IPEndPoint ClientEndPoint => (IPEndPoint)_client.Client.LocalEndPoint!;

        /// <summary>False once the receive loop has returned or faulted.</summary>
        public bool Running => !_loop.IsCompleted;

        public static Task<TestRelay> StartAsync(
            TimeSpan? cleanupInterval = null,
            Microsoft.Extensions.Logging.ILogger<UdpRelay>? logger = null,
            IOpsEventSink? events = null)
        {
            var time = new FakeTimeProvider();
            var relay = new UdpRelay(
                new IPEndPoint(IPAddress.Loopback, 0),
                Key,
                TimeSpan.FromMinutes(2),
                time,
                events ?? new NoopOpsEventSink(),
                "relay-test",
                logger ?? NullLogger<UdpRelay>.Instance,
                cleanupInterval);
            var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var ticket = new RelayTicket(Guid.NewGuid(), time.GetUtcNow() + TimeSpan.FromMinutes(2)).Sign(Key);
            return Task.FromResult(new TestRelay(relay, client, ticket, time));
        }

        /// <summary>The whole two-step bind, the way a client does it: ask, then present what came back.</summary>
        public async Task<bool> BindAsync(RelayRole role, byte[]? ticket = null)
        {
            ticket ??= Ticket;
            var answer = await ExchangeAsync(UdpControlPacket.CreateRelayBind((byte)role, [], ticket));
            if (!UdpControlPacket.TryParse(answer, out var type, out var body) || type != UdpControlPacketType.RelayBindChallenge)
            {
                return false;
            }

            return await BindWithAsync(role, body.ToArray(), ticket);
        }

        /// <summary>The second step on its own, for the tests that present a token the relay did not issue.</summary>
        public async Task<bool> BindWithAsync(RelayRole role, byte[] cookie, byte[]? ticket = null)
        {
            var answer = await ExchangeAsync(UdpControlPacket.CreateRelayBind((byte)role, cookie, ticket ?? Ticket), expectAnswer: false);
            return answer.Length > 0 &&
                UdpControlPacket.TryParse(answer, out var type, out _) &&
                type == UdpControlPacketType.RelayBindAck;
        }

        public async Task SendAsync(byte[] request) =>
            _ = await _client.SendAsync(request, Relay.LocalEndPoint, Ct);

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
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            {
                // A refused bind is answered with silence, so an absent packet is a result rather than an error —
                // but only where the caller says it does not need one. A lost packet that was expected fails here
                // instead of surfacing later as an unexplained "rejected".
                if (expectAnswer)
                {
                    throw new TimeoutException("The relay did not answer within five seconds: the bind or its acknowledgement was lost.");
                }

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

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), Ct);
        }

        return condition();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}

internal sealed class CountingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    private int _warnings;

    public int Warnings => Volatile.Read(ref _warnings);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
        {
            Interlocked.Increment(ref _warnings);
        }
    }
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
