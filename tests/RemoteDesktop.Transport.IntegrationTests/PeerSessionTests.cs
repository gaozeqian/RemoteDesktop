using RemoteDesktop.Application.Features;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// End to end over real sockets on loopback: signaling, reflection, hole punching or relay, QUIC with pinned mutual
/// TLS, the password handshake and channel dispatch.
/// </summary>
public sealed class PeerSessionTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Controller_connects_directly_and_is_granted_full_control()
    {
        await using var host = await TestDevice.StartAsync(server);
        await using var controller = await TestDevice.StartAsync(server);
        var started = WaitForHostSession(host);

        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);

        Assert.Equal(ConnectionPath.Direct, session.Path);

        // Everything this host can actually serve: all capabilities including the camera are granted for standard
        // control, minus the one it is not able to provide. This device registers no input injector and no agent
        // gateway, so it cannot raise Ctrl+Alt+Del, and the host does not claim otherwise — the grant is the host's
        // statement of what the session may do, and a capability the machine does not have is not one it can hand out.
        Assert.Equal(Permissions.All & ~Permissions.SecureAttention, session.Context.Grant.Permissions);
        var hostSide = await started.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(controller.Id, hostSide.Connection.RemoteDevice);
        Assert.Equal(ConnectionPath.Direct, hostSide.Connection.Path);

        // The server mints one session id per request and both ends report heartbeats under it. A direct connection
        // that dropped it made the console show the signaling row and a second, quality-only row for one session.
        Assert.False(string.IsNullOrEmpty(session.Context.Connection.RelaySessionId));
        Assert.Equal(session.Context.Connection.RelaySessionId, hostSide.Connection.RelaySessionId);
    }

    [Fact]
    public async Task Falls_back_to_relay_and_measures_round_trip_time()
    {
        await using var host = await TestDevice.StartAsync(server);
        await using var controller = await TestDevice.StartAsync(server, forceRelay: true);
        var started = WaitForHostSession(host);

        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);
        var rtt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RoundTripTimeChanged += (_, _) => rtt.TrySetResult();

        Assert.Equal(ConnectionPath.Relayed, session.Path);
        Assert.Equal(ConnectionPath.Relayed, (await started.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Connection.Path);
        await rtt.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.NotNull(session.RoundTripTime);
    }

    [Fact]
    public async Task Channels_reach_the_host_feature_module_through_the_relay()
    {
        await using var host = await TestDevice.StartAsync(server, forceRelay: false, new EchoModule());
        await using var controller = await TestDevice.StartAsync(server, forceRelay: true);
        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);

        await using var channel = await session.Context.Connection.OpenChannelAsync(ChannelType.Clipboard, bidirectional: true, Ct);
        var payload = "hello over quic"u8.ToArray();
        await channel.Stream.WriteAsync(payload, Ct);
        var echoed = new byte[payload.Length];
        await channel.Stream.ReadExactlyAsync(echoed, Ct);

        Assert.Equal(payload, echoed);
    }

    [Fact]
    public async Task Watch_mode_is_view_only_and_the_host_refuses_input_channels()
    {
        await using var host = await TestDevice.StartAsync(server, forceRelay: false, new EchoModule());
        await using var controller = await TestDevice.StartAsync(server);
        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.View, Ct);

        Assert.Equal(SessionMode.View, session.Context.Grant.Mode);
        Assert.Equal(Permissions.ViewOnly, session.Context.Grant.Permissions);

        // Even a controller that ignores its own grant gets nothing: the echo module requires Clipboard.
        await using var channel = await session.Context.Connection.OpenChannelAsync(ChannelType.Clipboard, bidirectional: true, Ct);
        var read = await ReadEchoOrFailAsync(channel.Stream);
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task Wrong_password_is_rejected_and_repeated_failures_lock_out()
    {
        await using var host = await TestDevice.StartAsync(server);
        await using var controller = await TestDevice.StartAsync(server);

        for (var i = 0; i < AuthAttemptLimiter.MaxFailuresPerDevice; i++)
        {
            var wrong = await Assert.ThrowsAsync<SessionException>(
                () => controller.Controller.ConnectAsync(host.Id, "wrong-password", SessionMode.Control, Ct));
            Assert.Equal(SessionFailure.BadPassword, wrong.Failure);
        }

        var locked = await Assert.ThrowsAsync<SessionException>(
            () => controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct));
        Assert.Equal(SessionFailure.TooManyAttempts, locked.Failure);
    }

    [Fact]
    public async Task Fixed_password_also_works()
    {
        await using var host = await TestDevice.StartAsync(server);
        await using var controller = await TestDevice.StartAsync(server);
        host.Credentials.FixedPassword = "unattended-secret";

        await using var session = await controller.Controller.ConnectAsync(host.Id, "unattended-secret", SessionMode.Control, Ct);

        Assert.Equal(SessionMode.Control, session.Context.Grant.Mode);
    }

    [Fact]
    public async Task Fixed_password_set_through_host_control_allows_unattended_access()
    {
        await using var host = await TestDevice.StartAsync(server);
        await using var controller = await TestDevice.StartAsync(server);
        var control = new LocalHostControl(host.Rendezvous, host.Credentials, host.HostSessions, null, false);
        await control.SetFixedPasswordAsync("my-long-lived-pass", Ct);

        await using var session = await controller.Controller.ConnectAsync(host.Id, "my-long-lived-pass", SessionMode.Control, Ct);

        Assert.Equal(SessionMode.Control, session.Context.Grant.Mode);
    }

    [Fact]
    public async Task Unknown_device_is_reported_offline()
    {
        await using var controller = await TestDevice.StartAsync(server);

        var error = await Assert.ThrowsAsync<PeerConnectException>(
            () => controller.Controller.ConnectAsync(new DeviceId(123_456_789), "x", SessionMode.Control, Ct));

        Assert.Equal(PeerConnectFailure.TargetOffline, error.Failure);
    }

    private static Task<SessionContext> WaitForHostSession(TestDevice host)
    {
        var started = new TaskCompletionSource<SessionContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.HostSessions.SessionStarted += (_, e) => started.TrySetResult(e.Session);
        return started.Task;
    }

    /// <summary>Returns the bytes echoed back; a refused stream is aborted on write/read or ends (0).</summary>
    private static async Task<int> ReadEchoOrFailAsync(Stream stream)
    {
        try
        {
            await stream.WriteAsync(new byte[] { 1 }, Ct);
            return await stream.ReadAsync(new byte[16], Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>Echoes everything back; requires the Clipboard permission so watch mode must refuse it.</summary>
    private sealed class EchoModule : IFeatureModule
    {
        public ChannelType ChannelType => ChannelType.Clipboard;

        public Permissions RequiredPermission => Permissions.Clipboard;

        public async Task HandleAsync(Transport.IChannel channel, SessionContext session, CancellationToken cancellationToken)
        {
            var buffer = new byte[1024];
            int read;
            while ((read = await channel.Stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await channel.Stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
    }
}
