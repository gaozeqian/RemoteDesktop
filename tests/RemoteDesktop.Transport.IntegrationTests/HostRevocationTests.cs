using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Clipboard;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Protocol;
using RemoteDesktop.TestSupport;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>The person at the host can take control away from a controller at any moment.</summary>
public sealed class HostRevocationTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Switching_a_session_to_watch_only_stops_input_and_clipboard_and_tells_the_controller()
    {
        using var injector = new RecordingInjector();
        var hostClipboard = new FakeClipboard();
        await using var host = await TestDevice.StartAsync(server, forceRelay: false,
            new InputChannelModule(injector, NullLogger<InputChannelModule>.Instance),
            new ClipboardChannelModule(hostClipboard, NullLoggerFactory.Instance));
        await using var controller = await TestDevice.StartAsync(server);
        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);
        Assert.True(session.Context.Grant.Allows(Permissions.ControlInput));

        var controllerClipboard = new FakeClipboard();
        var clipboardChannel = await session.Context.Connection.OpenChannelAsync(ChannelType.Clipboard, bidirectional: true, Ct);
        var clipboardRun = new ClipboardSynchronizer(controllerClipboard, NullLogger<ClipboardSynchronizer>.Instance).RunAsync(clipboardChannel.Stream, Ct);
        await using var sender = await InputSender.OpenAsync(session.Context.Connection, Ct);

        // Everything works before the downgrade.
        var before = new KeyEvent(0x1E, false, IsDown: true);
        sender.Send(before);
        await WaitForAsync(() => injector.Events.Contains(before), "input before the downgrade");
        controllerClipboard.Copy(new TextClipboardContent("before"));
        await hostClipboard.WaitForAsync(c => c is TextClipboardContent { Text: "before" });

        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.GrantChanged += (_, _) => changed.TrySetResult();
        // The host registers the session a moment after it answers the handshake, so wait for it rather than assume it.
        await WaitForAsync(() => host.HostSessions.ActiveSessions.Count == 1, "the session to appear on the host");
        var hostSession = host.HostSessions.ActiveSessions.Single();
        Assert.True(await host.HostSessions.DowngradeToViewAsync(hostSession.Id, Ct));

        // The controller learns about it...
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(SessionMode.View, session.Context.Grant.Mode);
        Assert.False(session.Context.Grant.Allows(Permissions.ControlInput));
        Assert.False(session.Context.Grant.Allows(Permissions.Clipboard));
        Assert.True(session.Context.Grant.Allows(Permissions.ViewScreen));
        Assert.Equal(SessionMode.View, hostSession.Grant.Mode);

        // ...and even a controller that ignores it gets nowhere: the open channels were closed on the host.
        await clipboardRun.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var hostSetCalls = hostClipboard.SetCalls;
        controllerClipboard.Copy(new TextClipboardContent("after"));
        var after = new KeyEvent(0x30, false, IsDown: true);
        for (var i = 0; i < 10; i++)
        {
            sender.Send(after);
            await Task.Delay(100, Ct);
        }

        Assert.DoesNotContain(after, injector.Events);
        Assert.Equal(hostSetCalls, hostClipboard.SetCalls);

        // A fresh attempt to open an input channel is refused too.
        await using var second = await InputSender.OpenAsync(session.Context.Connection, Ct);
        second.Send(after);
        await Task.Delay(500, Ct);
        Assert.DoesNotContain(after, injector.Events);
    }

    [Fact]
    public async Task The_host_can_disconnect_a_session()
    {
        await using var host = await TestDevice.StartAsync(server);
        await using var controller = await TestDevice.StartAsync(server);
        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Closed += (_, _) => closed.TrySetResult();
        await WaitForAsync(() => host.HostSessions.ActiveSessions.Count == 1, "the session to appear on the host");
        var id = host.HostSessions.ActiveSessions.Single().Id;

        Assert.True(host.HostSessions.Disconnect(id));

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await WaitForAsync(() => host.HostSessions.ActiveSessions.Count == 0, "the session to disappear from the host");
        Assert.False(host.HostSessions.Disconnect(id)); // already gone
    }

    [Fact]
    public async Task Unknown_sessions_are_reported_as_such()
    {
        await using var host = await TestDevice.StartAsync(server);

        Assert.False(await host.HostSessions.DowngradeToViewAsync(SessionId.New(), Ct));
        Assert.False(host.HostSessions.Disconnect(SessionId.New()));
    }

    private static Task WaitForAsync(Func<bool> condition, string what) =>
        TestWait.UntilAsync(condition, what, TimeSpan.FromSeconds(20));
}
