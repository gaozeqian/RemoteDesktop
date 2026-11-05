using System.Net;
using System.Net.WebSockets;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using RemoteDesktop.Domain;
using RemoteDesktop.Server.Rendezvous;
using RemoteDesktop.Signaling;
using RemoteDesktop.Signaling.Messages;
using RemoteDesktop.Transport.Identity;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// What the server hands out and to whom. Registration costs an identity and identities are the unit every other
/// budget here is counted in, so the limits in this file are what keeps the rest of them meaningful.
/// </summary>
public class KeyedTokenBucketTests
{
    private static readonly TimeSpan Refill = TimeSpan.FromSeconds(30);

    [Fact]
    public void A_burst_is_spent_and_then_refused()
    {
        var bucket = new KeyedTokenBucket<string>(3, Refill, new FakeTimeProvider());

        Assert.True(bucket.TryTake("one"));
        Assert.True(bucket.TryTake("one"));
        Assert.True(bucket.TryTake("one"));
        Assert.False(bucket.TryTake("one"));

        // What one key spends says nothing about another.
        Assert.True(bucket.TryTake("two"));
    }

    [Fact]
    public void Tokens_come_back_one_refill_at_a_time()
    {
        var time = new FakeTimeProvider();
        var bucket = new KeyedTokenBucket<string>(2, Refill, time);

        Assert.True(bucket.TryTake("one"));
        Assert.True(bucket.TryTake("one"));
        Assert.False(bucket.TryTake("one"));

        time.Advance(Refill);
        Assert.True(bucket.TryTake("one"));
        Assert.False(bucket.TryTake("one"));
    }

    /// <summary>
    /// A device that reconnects is not a new device, so its token comes back — otherwise a network behind one address
    /// would starve on its own reconnections.
    /// </summary>
    [Fact]
    public void A_refunded_token_can_be_spent_again()
    {
        var bucket = new KeyedTokenBucket<string>(1, Refill, new FakeTimeProvider());

        Assert.True(bucket.TryTake("one"));
        Assert.False(bucket.TryTake("one"));

        bucket.Refund("one");
        Assert.True(bucket.TryTake("one"));
    }

    [Fact]
    public void Refunding_cannot_bank_credit_beyond_a_full_burst()
    {
        var bucket = new KeyedTokenBucket<string>(2, Refill, new FakeTimeProvider());

        bucket.Refund("one");
        bucket.Refund("one");

        Assert.True(bucket.TryTake("one"));
        Assert.True(bucket.TryTake("one"));
        Assert.False(bucket.TryTake("one"));
    }

    [Fact]
    public void Forgetting_a_key_gives_it_a_full_burst_back()
    {
        var bucket = new KeyedTokenBucket<string>(1, Refill, new FakeTimeProvider());

        Assert.True(bucket.TryTake("one"));
        Assert.False(bucket.TryTake("one"));

        bucket.Forget("one");
        Assert.True(bucket.TryTake("one"));
    }

    /// <summary>
    /// The keys arrive from the network, so a limiter that only ever grows would be the attack itself.
    /// </summary>
    [Fact]
    public void Keys_nobody_has_touched_for_a_while_are_dropped()
    {
        var time = new FakeTimeProvider();
        var bucket = new KeyedTokenBucket<string>(1, Refill, time);

        for (var i = 0; i < 100; i++)
        {
            bucket.TryTake($"address-{i}");
        }

        Assert.Equal(100, bucket.Count);

        time.Advance(TimeSpan.FromHours(1));
        bucket.TryTake("somebody-else");

        Assert.Equal(1, bucket.Count);
    }
}

public class ConnectionQuotaTests
{
    [Fact]
    public void The_server_as_a_whole_stops_at_its_ceiling()
    {
        var quota = Quota(maxConnections: 2, perAddress: 10);
        var one = IPAddress.Parse("10.0.0.1");
        var two = IPAddress.Parse("10.0.0.2");

        Assert.True(quota.TryOpen(one));
        Assert.True(quota.TryOpen(two));
        Assert.False(quota.TryOpen(IPAddress.Parse("10.0.0.3")));
        Assert.Equal(2, quota.Open);
    }

    [Fact]
    public void One_address_cannot_take_the_whole_server()
    {
        var quota = Quota(maxConnections: 100, perAddress: 2);
        var noisy = IPAddress.Parse("10.0.0.1");

        Assert.True(quota.TryOpen(noisy));
        Assert.True(quota.TryOpen(noisy));
        Assert.False(quota.TryOpen(noisy));

        // The refusal must not have eaten a slot: somebody else still fits.
        Assert.True(quota.TryOpen(IPAddress.Parse("10.0.0.2")));
        Assert.Equal(3, quota.Open);
    }

    [Fact]
    public void Closing_gives_the_slot_back()
    {
        var quota = Quota(maxConnections: 1, perAddress: 1);
        var address = IPAddress.Parse("10.0.0.1");

        Assert.True(quota.TryOpen(address));
        Assert.False(quota.TryOpen(address));

        quota.Close(address);
        Assert.Equal(0, quota.Open);
        Assert.True(quota.TryOpen(address));
    }

    [Fact]
    public void Addresses_in_one_ipv6_subnet_share_a_limit()
    {
        var quota = Quota(maxConnections: 100, perAddress: 2);

        Assert.True(quota.TryOpen(IPAddress.Parse("2001:db8:1:2::1")));
        Assert.True(quota.TryOpen(IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd")));

        // A third address of the same /64 is the same subscriber; a different /64 is not.
        Assert.False(quota.TryOpen(IPAddress.Parse("2001:db8:1:2::99")));
        Assert.True(quota.TryOpen(IPAddress.Parse("2001:db8:1:3::1")));
    }

    [Fact]
    public void An_ipv4_mapped_address_counts_as_the_ipv4_address()
    {
        var quota = Quota(maxConnections: 100, perAddress: 1);

        Assert.True(quota.TryOpen(IPAddress.Parse("10.0.0.1")));
        Assert.False(quota.TryOpen(IPAddress.Parse("10.0.0.1").MapToIPv6()));
    }

    [Fact]
    public void Closing_through_another_spelling_of_the_same_source_gives_the_slot_back()
    {
        var quota = Quota(maxConnections: 100, perAddress: 1);
        var opened = IPAddress.Parse("2001:db8:1:2::1");

        Assert.True(quota.TryOpen(opened));
        quota.Close(IPAddress.Parse("2001:db8:1:2::1"));
        Assert.True(quota.TryOpen(IPAddress.Parse("2001:db8:1:2::2")));
    }

    private static ConnectionQuota Quota(int maxConnections, int perAddress) =>
        new(Options.Create(new RendezvousOptions { MaxConnections = maxConnections, MaxConnectionsPerAddress = perAddress }));
}

/// <summary>The limits as a client meets them: over its own server, not as unit tests of the pieces.</summary>
public class RendezvousLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_connection_past_the_quota_is_refused_before_it_becomes_a_device()
    {
        await using var server = await TestRendezvous.StartAsync(
            ("Rendezvous:MaxConnections", "2"),
            ("Rendezvous:MaxConnectionsPerAddress", "2"));
        var uri = TestRendezvous.SignalingUri(server);

        using var first = await RawSignaling.ConnectAsync(uri);
        using var second = await RawSignaling.ConnectAsync(uri);

        // The third is turned away at the door: no WebSocket, so nothing to keep in memory.
        await Assert.ThrowsAnyAsync<WebSocketException>(() => RawSignaling.ConnectAsync(uri));
    }

    [Fact]
    public async Task Registrations_from_one_address_are_throttled()
    {
        await using var server = await TestRendezvous.StartAsync(("Rendezvous:RegistrationBurst", "1"));
        var uri = TestRendezvous.SignalingUri(server);

        Assert.NotNull(await RawSignaling.RegisterAsync(uri));

        // The same address again, with a perfectly good certificate of its own: over the budget.
        using var socket = await RawSignaling.ConnectAsync(uri);
        Assert.Null(await RawSignaling.RegisterAsync(socket));
    }

    /// <summary>
    /// The registration budget counts a source the way the connection quota does: an IPv6 subscriber owns a whole /64
    /// and an IPv4-mapped address is its IPv4 form. Otherwise one machine rotates addresses and is never throttled.
    /// </summary>
    [Theory]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2:aaaa:bbbb:cccc:dddd")]
    [InlineData("10.0.0.1", "::ffff:10.0.0.1")]
    public async Task Registrations_from_one_network_are_throttled_whichever_address_inside_it_is_used(string first, string second)
    {
        await using var server = await TestRendezvous.StartAsync(
            ("Rendezvous:RegistrationBurst", "1"),
            ("Rendezvous:TrustedProxies:0", "127.0.0.1"));
        var uri = TestRendezvous.SignalingUri(server);

        using (var socket = await RawSignaling.ConnectAsync(uri, forwardedFor: first))
        {
            Assert.NotNull(await RawSignaling.RegisterAsync(socket));
        }

        using (var socket = await RawSignaling.ConnectAsync(uri, forwardedFor: second))
        {
            Assert.Null(await RawSignaling.RegisterAsync(socket));
        }
    }

    [Fact]
    public async Task Registrations_from_different_networks_do_not_share_a_budget()
    {
        await using var server = await TestRendezvous.StartAsync(
            ("Rendezvous:RegistrationBurst", "1"),
            ("Rendezvous:TrustedProxies:0", "127.0.0.1"));
        var uri = TestRendezvous.SignalingUri(server);

        using (var socket = await RawSignaling.ConnectAsync(uri, forwardedFor: "2001:db8:1:2::1"))
        {
            Assert.NotNull(await RawSignaling.RegisterAsync(socket));
        }

        using (var socket = await RawSignaling.ConnectAsync(uri, forwardedFor: "2001:db8:1:3::1"))
        {
            Assert.NotNull(await RawSignaling.RegisterAsync(socket));
        }
    }

    /// <summary>
    /// With no trusted proxy configured, forwarded headers are not believed at all. Clearing the framework's default
    /// proxies is not enough: with both lists empty the middleware trusts every caller, so a caller could write its
    /// own source address and get a fresh registration budget (and, with no node token, look like a loopback caller).
    /// </summary>
    [Fact]
    public async Task A_caller_cannot_choose_its_source_address_when_no_proxy_is_trusted()
    {
        await using var server = await TestRendezvous.StartAsync(("Rendezvous:RegistrationBurst", "1"));
        var uri = TestRendezvous.SignalingUri(server);

        using (var socket = await RawSignaling.ConnectAsync(uri, forwardedFor: "2001:db8:1:2::1"))
        {
            Assert.NotNull(await RawSignaling.RegisterAsync(socket));
        }

        // Same real caller, a different address written into the header: it must still count against one budget.
        using (var socket = await RawSignaling.ConnectAsync(uri, forwardedFor: "198.51.100.7"))
        {
            Assert.Null(await RawSignaling.RegisterAsync(socket));
        }
    }

    [Fact]
    public async Task Connect_requests_from_one_device_are_throttled()
    {
        await using var server = await TestRendezvous.StartAsync(
            ("Rendezvous:ConnectRequestBurst", "1"),
            ("Rendezvous:ConnectRequestRefill", "01:00:00"));
        await using var device = await TestDevice.StartAsync(TestRendezvous.SignalingUri(server));

        // A code nothing is registered under: the first request is answered at once, with no host to wait for.
        var offline = new DeviceId(111_222_333);
        var first = await Assert.ThrowsAsync<PeerConnectException>(() => device.Rendezvous.RequestConnectionAsync(offline, [], Ct));
        Assert.Equal(PeerConnectFailure.TargetOffline, first.Failure);

        // The second one never reaches that decision — the budget is what refuses it now.
        var second = await Assert.ThrowsAsync<PeerConnectException>(() => device.Rendezvous.RequestConnectionAsync(offline, [], Ct));
        Assert.Equal(PeerConnectFailure.Rejected, second.Failure);
    }
}
