using System.Net.Quic;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;
using RemoteDesktop.TestSupport;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// What the host does with the channels of a running session: it caps how many may be open at once, and the person
/// at the machine can withdraw one permission (closing exactly the channels that need it) without ending the session.
/// </summary>
public sealed class HostSessionChannelTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    // HostSessionService.MaxChannelsPerSession is a private constant; the test follows its value on purpose, so that
    // changing the ceiling is a deliberate act that touches this test as well.
    private const int MaxChannelsPerSession = 24;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_session_cannot_hold_more_than_the_channel_limit_and_a_closed_channel_frees_its_place()
    {
        var clipboard = new HoldModule(ChannelType.Clipboard, Permissions.Clipboard);
        await using var host = await TestDevice.StartAsync(server, forceRelay: false, clipboard);
        await using var controller = await TestDevice.StartAsync(server);
        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);
        await WaitForAsync(() => host.HostSessions.ActiveSessions.Count == 1, "the session to appear on the host");
        var id = host.HostSessions.ActiveSessions.Single().Id;
        var connection = session.Context.Connection;

        var open = new List<IChannel>();
        try
        {
            for (var i = 0; i < MaxChannelsPerSession; i++)
            {
                open.Add(await connection.OpenChannelAsync(ChannelType.Clipboard, bidirectional: true, Ct));
            }

            await WaitForAsync(() => clipboard.Running == MaxChannelsPerSession, "every channel up to the limit to be served");
            Assert.True(host.HostSessions.IsChannelOpen(id, ChannelType.Clipboard));

            // One over the limit: the host closes it without handing it to the module.
            await using var surplus = await connection.OpenChannelAsync(ChannelType.Clipboard, bidirectional: true, Ct);
            await WaitUntilClosedByPeerAsync(surplus.Stream);
            Assert.Equal(MaxChannelsPerSession, clipboard.Started);
            Assert.Equal(MaxChannelsPerSession, clipboard.Running);

            // Neither the session nor its other channels suffered.
            Assert.Single(host.HostSessions.ActiveSessions);

            // A channel that ends gives its place back.
            await open[0].DisposeAsync();
            open.RemoveAt(0);
            await WaitForAsync(() => clipboard.Running == MaxChannelsPerSession - 1, "the closed channel to leave the host");

            // The place frees when the finished channel's task completes, a moment after the module returned, so an
            // attempt right at that moment may still find the session full. Trying again must succeed.
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (clipboard.Running < MaxChannelsPerSession)
            {
                Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the replacement channel to be served.");
                open.Add(await connection.OpenChannelAsync(ChannelType.Clipboard, bidirectional: true, Ct));
                await Task.Delay(300, Ct);
            }

            Assert.Equal(MaxChannelsPerSession + 1, clipboard.Started);
        }
        finally
        {
            foreach (var channel in open)
            {
                await channel.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Revoking_a_permission_closes_only_the_channels_that_need_it_and_the_session_goes_on()
    {
        var clipboard = new HoldModule(ChannelType.Clipboard, Permissions.Clipboard);
        var files = new HoldModule(ChannelType.FileTransfer, Permissions.FileTransfer);
        await using var host = await TestDevice.StartAsync(server, forceRelay: false, clipboard, files);
        await using var controller = await TestDevice.StartAsync(server);
        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);
        await WaitForAsync(() => host.HostSessions.ActiveSessions.Count == 1, "the session to appear on the host");
        var hostSession = host.HostSessions.ActiveSessions.Single();
        var connection = session.Context.Connection;
        Assert.True(session.Context.Grant.Allows(Permissions.Clipboard));
        Assert.True(session.Context.Grant.Allows(Permissions.FileTransfer));

        await using var clipboardChannel = await connection.OpenChannelAsync(ChannelType.Clipboard, bidirectional: true, Ct);
        await using var fileChannel = await connection.OpenChannelAsync(ChannelType.FileTransfer, bidirectional: true, Ct);
        await WaitForAsync(() => host.HostSessions.IsChannelOpen(hostSession.Id, ChannelType.Clipboard), "the clipboard channel");
        await WaitForAsync(() => host.HostSessions.IsChannelOpen(hostSession.Id, ChannelType.FileTransfer), "the file channel");

        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.GrantChanged += (_, _) => changed.TrySetResult();

        Assert.True(await host.HostSessions.RevokeAsync(hostSession.Id, Permissions.Clipboard, Ct));

        // The controller is told, and sees exactly the one permission missing.
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.False(session.Context.Grant.Allows(Permissions.Clipboard));
        Assert.True(session.Context.Grant.Allows(Permissions.FileTransfer));
        Assert.True(session.Context.Grant.Allows(Permissions.ControlInput));
        Assert.Equal(SessionMode.Control, session.Context.Grant.Mode);
        Assert.False(hostSession.Grant.Allows(Permissions.Clipboard));
        Assert.True(hostSession.Grant.Allows(Permissions.FileTransfer));

        // The channel that needed it was closed on the host; the other one was left alone.
        await WaitForAsync(() => clipboard.Running == 0, "the clipboard channel to be closed");
        await WaitForAsync(() => !host.HostSessions.IsChannelOpen(hostSession.Id, ChannelType.Clipboard), "the clipboard channel to leave the session");
        Assert.True(host.HostSessions.IsChannelOpen(hostSession.Id, ChannelType.FileTransfer));
        Assert.Equal(1, files.Running);
        Assert.Single(host.HostSessions.ActiveSessions);

        // A new attempt is refused before the module sees it, while the still-granted feature keeps working.
        await using var refused = await connection.OpenChannelAsync(ChannelType.Clipboard, bidirectional: true, Ct);
        await WaitUntilClosedByPeerAsync(refused.Stream);
        Assert.Equal(1, clipboard.Started);
        await using var another = await connection.OpenChannelAsync(ChannelType.FileTransfer, bidirectional: true, Ct);
        await WaitForAsync(() => files.Running == 2, "a new file channel to be served");
    }

    [Fact]
    public async Task Revoking_from_an_unknown_session_reports_it_and_changes_nothing()
    {
        await using var host = await TestDevice.StartAsync(server);
        await using var controller = await TestDevice.StartAsync(server);
        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);
        await WaitForAsync(() => host.HostSessions.ActiveSessions.Count == 1, "the session to appear on the host");
        var hostSession = host.HostSessions.ActiveSessions.Single();
        var before = hostSession.Grant;

        Assert.False(await host.HostSessions.RevokeAsync(SessionId.New(), Permissions.Clipboard, Ct));
        Assert.False(host.HostSessions.IsChannelOpen(SessionId.New(), ChannelType.Clipboard));

        Assert.Equal(before, hostSession.Grant);
    }

    [Fact]
    public async Task Revoking_from_all_sessions_reaches_every_running_session_and_is_harmless_without_any()
    {
        var clipboard = new HoldModule(ChannelType.Clipboard, Permissions.Clipboard);
        await using var host = await TestDevice.StartAsync(server, forceRelay: false, clipboard);
        await using var controller = await TestDevice.StartAsync(server);

        await host.HostSessions.RevokeFromAllAsync(Permissions.Clipboard, Ct);

        await using var first = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);
        await using var second = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);
        await WaitForAsync(() => host.HostSessions.ActiveSessions.Count == 2, "both sessions to appear on the host");
        await using var firstChannel = await first.Context.Connection.OpenChannelAsync(ChannelType.Clipboard, bidirectional: true, Ct);
        await using var secondChannel = await second.Context.Connection.OpenChannelAsync(ChannelType.Clipboard, bidirectional: true, Ct);
        await WaitForAsync(() => clipboard.Running == 2, "both clipboard channels to be served");

        var firstChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.GrantChanged += (_, _) => firstChanged.TrySetResult();
        second.GrantChanged += (_, _) => secondChanged.TrySetResult();

        await host.HostSessions.RevokeFromAllAsync(Permissions.Clipboard, Ct);

        await Task.WhenAll(firstChanged.Task, secondChanged.Task).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.False(first.Context.Grant.Allows(Permissions.Clipboard));
        Assert.False(second.Context.Grant.Allows(Permissions.Clipboard));
        Assert.All(host.HostSessions.ActiveSessions, s => Assert.False(s.Grant.Allows(Permissions.Clipboard)));
        await WaitForAsync(() => clipboard.Running == 0, "both clipboard channels to be closed");
        await WaitForAsync(
            () => host.HostSessions.ActiveSessions.All(s => !host.HostSessions.IsChannelOpen(s.Id, ChannelType.Clipboard)),
            "both clipboard channels to leave their sessions");
        Assert.Equal(2, host.HostSessions.ActiveSessions.Count);
    }

    /// <summary>A refused channel is closed by the host: the other side reads the end of the stream or an abort.</summary>
    private static async Task WaitUntilClosedByPeerAsync(Stream stream)
    {
        try
        {
            var read = await stream.ReadAsync(new byte[16], Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(0, read);
        }
        catch (Exception ex) when (ex is IOException or QuicException)
        {
            // Closed by an abort rather than a clean end.
        }
    }

    private static Task WaitForAsync(Func<bool> condition, string what) =>
        TestWait.UntilAsync(condition, what, TimeSpan.FromSeconds(20));

    /// <summary>Serves a channel by doing nothing until it is closed or cancelled, and counts how many are being served.</summary>
    private sealed class HoldModule(ChannelType type, Permissions required) : IFeatureModule
    {
        private int _started;
        private int _running;

        public ChannelType ChannelType => type;

        public Permissions RequiredPermission => required;

        public int Started => Volatile.Read(ref _started);

        public int Running => Volatile.Read(ref _running);

        public async Task HandleAsync(IChannel channel, SessionContext session, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _started);
            Interlocked.Increment(ref _running);
            try
            {
                var buffer = new byte[16];
                while (await channel.Stream.ReadAsync(buffer, cancellationToken) > 0)
                {
                }
            }
            catch (Exception ex) when (ex is IOException or QuicException)
            {
                // The controller closed or aborted its end.
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }
}
