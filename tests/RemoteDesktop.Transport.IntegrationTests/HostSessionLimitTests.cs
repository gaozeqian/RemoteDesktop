using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// The host caps how many controllers may be in a session. A controller over the cap has to be told so by the
/// handshake itself: an "accepted" answer followed by a closed connection reads as a network fault.
/// </summary>
public sealed class HostSessionLimitTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_controller_over_the_session_limit_is_refused_with_a_clear_reason_and_existing_sessions_survive()
    {
        await using var host = await TestDevice.StartAsync(server);
        host.HostSessions.MaxSessions = 2;
        await using var controller = await TestDevice.StartAsync(server);

        await using var first = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);
        await using var second = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);
        await WaitForAsync(() => host.HostSessions.ActiveSessions.Count == 2, "both sessions to appear on the host");

        var refused = await Assert.ThrowsAsync<SessionException>(
            () => controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct));
        Assert.Equal(SessionFailure.TooManySessions, refused.Failure);

        // Nothing about the sessions that were already there changed.
        Assert.Equal(2, host.HostSessions.ActiveSessions.Count);
        await Task.Delay(200, Ct);
        Assert.Equal(2, host.HostSessions.ActiveSessions.Count);

        // A freed place is usable again, and the refusal did not count as a failed password attempt.
        await second.DisposeAsync();
        await WaitForAsync(() => host.HostSessions.ActiveSessions.Count == 1, "the second session to leave the host");
        await using var third = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);
        await WaitForAsync(() => host.HostSessions.ActiveSessions.Count == 2, "the replacement session to appear on the host");
    }

    /// <summary>
    /// Handshakes that pass authentication at the same moment must not all be told "accepted" and then have the
    /// losers dropped: the reservation is taken before AuthResult, so the refusal is part of the handshake.
    /// </summary>
    [Fact]
    public async Task Simultaneous_handshakes_over_the_limit_are_refused_in_the_handshake()
    {
        await using var host = await TestDevice.StartAsync(server);
        host.HostSessions.MaxSessions = 2;
        await using var controller = await TestDevice.StartAsync(server);
        var password = host.Credentials.TemporaryPassword;

        var attempts = Enumerable.Range(0, 3)
            .Select(async _ =>
            {
                try
                {
                    var session = await controller.Controller.ConnectAsync(host.Id, password, SessionMode.Control, Ct);
                    return (Session: session, Failure: (SessionFailure?)null);
                }
                catch (SessionException ex)
                {
                    return (Session: (RemoteSession?)null, Failure: (SessionFailure?)ex.Failure);
                }
            })
            .ToArray();

        var results = await Task.WhenAll(attempts);
        var accepted = results.Where(result => result.Session is not null).Select(result => result.Session!).ToArray();
        try
        {
            Assert.Equal(2, accepted.Length);
            var refused = Assert.Single(results, result => result.Session is null);
            Assert.Equal(SessionFailure.TooManySessions, refused.Failure);
            await WaitForAsync(() => host.HostSessions.ActiveSessions.Count == 2, "both accepted sessions to appear on the host");
        }
        finally
        {
            foreach (var session in accepted)
            {
                await session.DisposeAsync();
            }
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(50, Ct);
        }
    }
}
