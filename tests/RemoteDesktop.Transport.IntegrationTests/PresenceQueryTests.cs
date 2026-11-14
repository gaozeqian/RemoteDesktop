using System.Net.WebSockets;
using RemoteDesktop.Domain;
using RemoteDesktop.Server.Rendezvous;
using RemoteDesktop.TestSupport;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// The presence query, which is how the client draws an online dot beside a remembered device. What matters here is
/// the shape of the answer: only registered devices are named, and a server that says nothing is never mistaken for a
/// server that said "offline".
/// </summary>
public class PresenceQueryTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    /// <summary>A code nothing is registered under, and one no server will ever allocate by accident in a test run.</summary>
    private static readonly DeviceId Unregistered = new(111_222_333);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_registered_device_is_named_and_an_unregistered_code_is_not()
    {
        await using var target = await TestDevice.StartAsync(server);
        await using var asker = await TestDevice.StartAsync(server);

        var online = await asker.Rendezvous.QueryPresenceAsync([target.Id, Unregistered], Ct);

        Assert.NotNull(online);
        Assert.Contains(target.Id, online);
        Assert.DoesNotContain(Unregistered, online);
    }

    [Fact]
    public async Task A_device_that_disconnects_stops_being_named()
    {
        var target = await TestDevice.StartAsync(server);
        await using var asker = await TestDevice.StartAsync(server);

        var targetId = target.Id;
        var before = await asker.Rendezvous.QueryPresenceAsync([targetId], Ct);
        Assert.NotNull(before);
        Assert.Contains(targetId, before);

        await target.DisposeAsync();

        // Disposing the host closes the socket but does not wait for the server to observe the close, so the device
        // can still be in the presence map when the next query arrives. Poll briefly instead of requiring it to be
        // gone at once; the queries stay inside the server's burst of ten, so none of them is refused.
        var after = await asker.Rendezvous.QueryPresenceAsync([targetId], Ct);
        Assert.NotNull(after);
        for (var attempt = 0; after.Contains(targetId) && attempt < 6; attempt++)
        {
            await Task.Delay(500, Ct);
            after = await asker.Rendezvous.QueryPresenceAsync([targetId], Ct);
            Assert.NotNull(after);
        }

        Assert.DoesNotContain(targetId, after);
    }

    [Fact]
    public async Task Asking_about_no_devices_needs_no_answer()
    {
        await using var device = await TestDevice.StartAsync(server);

        var online = await device.Rendezvous.QueryPresenceAsync([], Ct);

        Assert.NotNull(online);
        Assert.Empty(online);
    }
}

/// <summary>The abuse control on the presence query: a client cannot watch a large list of codes on a timer.</summary>
public class PresenceRateLimiterTests
{
    [Fact]
    public void A_burst_is_spent_and_then_queries_are_refused()
    {
        var time = new TestClock(DateTimeOffset.UnixEpoch);
        var limiter = new PresenceRateLimiter(time);
        using var session = Session(100_000_001);

        for (var i = 0; i < 10; i++)
        {
            Assert.True(limiter.TryAcquire(session), $"query {i + 1} of the burst should be allowed");
        }

        Assert.False(limiter.TryAcquire(session));
    }

    [Fact]
    public void The_bucket_refills_over_time()
    {
        var time = new TestClock(DateTimeOffset.UnixEpoch);
        var limiter = new PresenceRateLimiter(time);
        using var session = Session(100_000_002);

        for (var i = 0; i < 10; i++)
        {
            limiter.TryAcquire(session);
        }

        Assert.False(limiter.TryAcquire(session));
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.True(limiter.TryAcquire(session));
        Assert.False(limiter.TryAcquire(session));
    }

    [Fact]
    public void Forgetting_another_session_leaves_this_ones_budget_alone()
    {
        var time = new TestClock(DateTimeOffset.UnixEpoch);
        var limiter = new PresenceRateLimiter(time);
        using var old = Session(100_000_003);
        using var current = Session(100_000_003);

        // The reconnect installed a fresh budget, and the dying session's cleanup must not throw it away.
        Assert.True(limiter.TryAcquire(current));
        limiter.Forget(old);
        Assert.True(limiter.TryAcquire(current));
    }

    private static DeviceSession Session(uint deviceId) =>
        new(deviceId, "fingerprint", capabilities: 0, WebSocket.CreateFromStream(Stream.Null, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero));
}
