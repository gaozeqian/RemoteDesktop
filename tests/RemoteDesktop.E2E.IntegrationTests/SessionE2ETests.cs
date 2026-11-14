using RemoteDesktop.Application.Features;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Transport;
using RemoteDesktop.TestSupport;

namespace RemoteDesktop.E2E.IntegrationTests;

/// <summary>
/// End-to-end integration tests covering server cluster orchestration, rendezvous discovery,
/// SRP-6a authentication (both temporary and unattended fixed password), QUIC P2P & relay fallback,
/// access grant negotiation, and session lifecycle.
/// </summary>
public sealed class SessionE2ETests(EndToEndClusterFixture cluster) : IClassFixture<EndToEndClusterFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Host_and_controller_register_with_rendezvous_and_establish_p2p_session_with_temporary_password()
    {
        await using var host = await cluster.StartNodeAsync(cancellationToken: Ct);
        await using var controller = await cluster.StartNodeAsync(cancellationToken: Ct);

        await using var session = await controller.ConnectAsync(
            host.Id,
            host.Credentials.TemporaryPassword,
            SessionMode.Control,
            Ct);

        Assert.NotNull(session);
        Assert.NotEqual(default, session.Context.Id);
        Assert.Equal(SessionRole.Controller, session.Context.Role);
        Assert.Equal(SessionMode.Control, session.Context.Grant.Mode);
        Assert.True(session.Context.Grant.Allows(Permissions.ControlInput));

        // Verify that the host registers this active session
        await WaitForAsync(
            () => host.HostSessions.ActiveSessions.Any(s => s.Id == session.Context.Id),
            "host active session registration",
            Ct);

        // Verify that RTT measurement runs over ping/pong
        var rttReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (session.RoundTripTime is not null)
        {
            rttReceived.TrySetResult();
        }
        else
        {
            session.RoundTripTimeChanged += (_, _) => rttReceived.TrySetResult();
        }

        await rttReceived.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.NotNull(session.RoundTripTime);
        Assert.True(session.RoundTripTime.Value >= TimeSpan.Zero);

        // Controller closes session
        await session.DisposeAsync();

        await WaitForAsync(
            () => !host.HostSessions.ActiveSessions.Any(s => s.Id == session.Context.Id),
            "host active session cleanup",
            Ct);
    }

    [Fact]
    public async Task Session_can_be_established_via_relay_when_forced()
    {
        var relayOptions = new EndToEndNodeOptions { ForceRelay = true };
        await using var host = await cluster.StartNodeAsync(relayOptions, Ct);
        await using var controller = await cluster.StartNodeAsync(relayOptions, Ct);

        await using var session = await controller.ConnectAsync(
            host.Id,
            host.Credentials.TemporaryPassword,
            SessionMode.Control,
            Ct);

        Assert.NotNull(session);
        Assert.Equal(ConnectionPath.Relayed, session.Path);
        Assert.Equal(SessionMode.Control, session.Context.Grant.Mode);

        await WaitForAsync(
            () => host.HostSessions.ActiveSessions.Any(s => s.Id == session.Context.Id),
            "host relay session registration",
            Ct);
    }

    [Fact]
    public async Task Session_authenticates_with_fixed_password_in_unattended_mode()
    {
        const string fixedPassword = "UnattendedSecretKey2026!";
        var hostOptions = new EndToEndNodeOptions { FixedPassword = fixedPassword };

        await using var host = await cluster.StartNodeAsync(hostOptions, Ct);
        await using var controller = await cluster.StartNodeAsync(cancellationToken: Ct);

        await using var session = await controller.ConnectAsync(
            host.Id,
            fixedPassword,
            SessionMode.Control,
            Ct);

        Assert.NotNull(session);
        Assert.Equal(SessionMode.Control, session.Context.Grant.Mode);
        Assert.True(session.Context.Grant.Allows(Permissions.ControlInput));

        await WaitForAsync(
            () => host.HostSessions.ActiveSessions.Any(s => s.Id == session.Context.Id),
            "host unattended session registration",
            Ct);
    }

    [Fact]
    public async Task Session_mode_view_grants_view_only_without_input_permissions()
    {
        await using var host = await cluster.StartNodeAsync(cancellationToken: Ct);
        await using var controller = await cluster.StartNodeAsync(cancellationToken: Ct);

        await using var session = await controller.ConnectAsync(
            host.Id,
            host.Credentials.TemporaryPassword,
            SessionMode.View,
            Ct);

        Assert.NotNull(session);
        Assert.Equal(SessionMode.View, session.Context.Grant.Mode);
        Assert.False(session.Context.Grant.Allows(Permissions.ControlInput));
    }

    [Fact]
    public async Task Invalid_password_is_rejected_and_session_fails()
    {
        await using var host = await cluster.StartNodeAsync(cancellationToken: Ct);
        await using var controller = await cluster.StartNodeAsync(cancellationToken: Ct);

        var ex = await Assert.ThrowsAsync<SessionException>(() =>
            controller.ConnectAsync(host.Id, "WrongPassword123", SessionMode.Control, Ct));

        Assert.Equal(SessionFailure.BadPassword, ex.Failure);
        Assert.Empty(host.HostSessions.ActiveSessions);
    }

    [Fact]
    public async Task Brute_force_password_attempts_trigger_rate_limiting()
    {
        await using var host = await cluster.StartNodeAsync(cancellationToken: Ct);
        await using var controller = await cluster.StartNodeAsync(cancellationToken: Ct);

        // MaxFailuresPerDevice is 5; fail 5 times
        for (var i = 0; i < AuthAttemptLimiter.MaxFailuresPerDevice; i++)
        {
            var ex = await Assert.ThrowsAsync<SessionException>(() =>
                controller.ConnectAsync(host.Id, $"WrongPassword_{i}", SessionMode.Control, Ct));
            Assert.Equal(SessionFailure.BadPassword, ex.Failure);
        }

        // The 6th attempt should be blocked by rate limiter
        var rateLimitedEx = await Assert.ThrowsAsync<SessionException>(() =>
            controller.ConnectAsync(host.Id, "WrongPassword_Blocked", SessionMode.Control, Ct));

        Assert.Equal(SessionFailure.TooManyAttempts, rateLimitedEx.Failure);
    }

    [Fact]
    public async Task Host_can_dynamically_downgrade_session_to_view_only()
    {
        await using var host = await cluster.StartNodeAsync(cancellationToken: Ct);
        await using var controller = await cluster.StartNodeAsync(cancellationToken: Ct);

        await using var session = await controller.ConnectAsync(
            host.Id,
            host.Credentials.TemporaryPassword,
            SessionMode.Control,
            Ct);

        Assert.True(session.Context.Grant.Allows(Permissions.ControlInput));

        // The controller learns the id from AuthResult, but the host's own table is filled after that answer is
        // written: DowngradeToViewAsync looks the id up, so calling it the moment ConnectAsync returns is a race.
        await WaitForHostRegistrationAsync(host, controller, Ct);

        var grantChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.GrantChanged += (_, _) =>
        {
            if (session.Context.Grant.Mode == SessionMode.View)
            {
                grantChanged.TrySetResult();
            }
        };

        var downgraded = await host.HostSessions.DowngradeToViewAsync(session.Context.Id, Ct);
        Assert.True(downgraded);

        await grantChanged.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(SessionMode.View, session.Context.Grant.Mode);
        Assert.False(session.Context.Grant.Allows(Permissions.ControlInput));
    }

    [Fact]
    public async Task Host_disconnecting_session_triggers_controller_closed_event()
    {
        await using var host = await cluster.StartNodeAsync(cancellationToken: Ct);
        await using var controller = await cluster.StartNodeAsync(cancellationToken: Ct);

        await using var session = await controller.ConnectAsync(
            host.Id,
            host.Credentials.TemporaryPassword,
            SessionMode.Control,
            Ct);

        var closedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Closed += (_, _) => closedTcs.TrySetResult();

        // Same registration window as the downgrade above: Disconnect reports false for a session it has not been
        // told about yet, which says nothing about whether the host can end it.
        await WaitForHostRegistrationAsync(host, controller, Ct);

        var disconnected = host.HostSessions.Disconnect(session.Context.Id);
        Assert.True(disconnected);

        await closedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task Multiple_controllers_can_concurrently_connect_to_one_host()
    {
        await using var host = await cluster.StartNodeAsync(cancellationToken: Ct);
        await using var controller1 = await cluster.StartNodeAsync(cancellationToken: Ct);
        await using var controller2 = await cluster.StartNodeAsync(cancellationToken: Ct);

        await using var session1 = await controller1.ConnectAsync(
            host.Id,
            host.Credentials.TemporaryPassword,
            SessionMode.Control,
            Ct);

        await using var session2 = await controller2.ConnectAsync(
            host.Id,
            host.Credentials.TemporaryPassword,
            SessionMode.View,
            Ct);

        Assert.Equal(SessionMode.Control, session1.Context.Grant.Mode);
        Assert.Equal(SessionMode.View, session2.Context.Grant.Mode);

        await WaitForAsync(
            () => host.HostSessions.ActiveSessions.Count == 2,
            "two concurrent active sessions",
            Ct);

        await session1.DisposeAsync();

        await WaitForAsync(
            () => host.HostSessions.ActiveSessions.Count == 1,
            "one remaining active session after first disconnect",
            Ct);

        await session2.DisposeAsync();

        await WaitForAsync(
            () => host.HostSessions.ActiveSessions.Count == 0,
            "all active sessions cleaned up",
            Ct);
    }

    /// <summary>
    /// Waits until the host has a row for this controller. The host adds the session to its own table after it has
    /// written AuthResult, so anything that looks the session up by id (Disconnect, DowngradeToViewAsync) can run
    /// before the row exists and report "no such session" — a false negative, not a product fault.
    /// </summary>
    private static Task WaitForHostRegistrationAsync(
        EndToEndNode host,
        EndToEndNode controller,
        CancellationToken cancellationToken) =>
        WaitForAsync(
            () => host.HostSessions.ActiveSessions.Any(s => s.Connection.RemoteDevice == controller.Id),
            "the host to register the controller's session",
            cancellationToken);

    private static Task WaitForAsync(Func<bool> condition, string what, CancellationToken cancellationToken) =>
        TestWait.UntilAsync(condition, what, TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(50), cancellationToken);
}
