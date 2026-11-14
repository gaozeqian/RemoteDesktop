using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Agent;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Media;
using RemoteDesktop.Platform.Windows.MediaFoundation;
using RemoteDesktop.Protocol;
using RemoteDesktop.TestSupport;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// The background-service arrangement end to end: the host service relays the controller's channels to an agent (here
/// in-process), and keeps the controller's session going when that agent is replaced, as happens when the input moves
/// to the login screen or a UAC prompt.
/// </summary>
public sealed class AgentForwardingSessionTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_controller_keeps_receiving_video_when_the_agent_is_replaced()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation codecs are Windows only.");
        var codecs = new VideoCodecSelector([new MediaFoundationH264EncoderFactory(hardware: false)], [new MediaFoundationH264DecoderFactory()]);
        Assert.SkipUnless(codecs.GetDecoderCandidates(VideoCodec.H264).Any(), "No H.264 decoder.");

        using var injector = new RecordingInjector();
        using var gateway = new InProcessAgentGateway(
        [
            new VideoChannelModule(new FakeDisplays(), new FakeCapturerFactory(), codecs, TimeProvider.System, NullLogger<VideoChannelModule>.Instance),
            new InputChannelModule(injector, NullLogger<InputChannelModule>.Instance),
        ]);

        await using var host = await TestDevice.StartAsync(server, forceRelay: false, ServiceModules(gateway));
        await using var controller = await TestDevice.StartAsync(server);
        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);

        var pictures = 0;
        var streams = 0;
        var closed = false;
        await using var viewer = await DesktopViewer.StartAsync(session.Context.Connection, codecs, 30, TimeProvider.System, NullLogger.Instance, Ct);
        viewer.FrameReady += (_, _) => Interlocked.Increment(ref pictures);
        viewer.StreamInfoChanged += (_, _) => Interlocked.Increment(ref streams);
        viewer.Closed += (_, _) => closed = true;

        await WaitForAsync(() => Volatile.Read(ref pictures) >= 10, "the first pictures");
        Assert.Equal(1, Volatile.Read(ref streams));

        await gateway.RestartAgentAsync();
        var before = Volatile.Read(ref pictures);

        await WaitForAsync(() => Volatile.Read(ref pictures) >= before + 10, "pictures from the replacement agent");
        Assert.Equal(2, Volatile.Read(ref streams)); // the new agent announced itself with a fresh stream
        Assert.False(closed);
        Assert.Equal(2, gateway.Generations);
    }

    [Fact]
    public async Task Input_reaches_the_agent_before_and_after_it_is_replaced()
    {
        using var injector = new RecordingInjector();
        using var gateway = new InProcessAgentGateway([new InputChannelModule(injector, NullLogger<InputChannelModule>.Instance)]);

        await using var host = await TestDevice.StartAsync(server, forceRelay: false, ServiceModules(gateway));
        await using var controller = await TestDevice.StartAsync(server);
        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);

        await using var sender = await InputSender.OpenAsync(session.Context.Connection, Ct);
        var first = new KeyEvent(0x1E, false, IsDown: true);
        var second = new KeyEvent(0x30, false, IsDown: true);
        sender.Send(first);
        await WaitForAsync(() => injector.Events.Contains(first), "the first key at the first agent");

        await gateway.RestartAgentAsync();

        // Anything sent while the service reconnects is dropped on purpose (stale input), so keep trying.
        await WaitForAsync(() =>
        {
            sender.Send(second);
            return injector.Events.Contains(second);
        }, "a key at the replacement agent");

        // The first agent went away with 'A' still held: it was released rather than left stuck.
        Assert.Contains(new KeyEvent(0x1E, false, IsDown: false), injector.Events);
    }

    [Fact]
    public async Task Watch_mode_never_reaches_the_agent()
    {
        using var injector = new RecordingInjector();
        using var gateway = new InProcessAgentGateway([new InputChannelModule(injector, NullLogger<InputChannelModule>.Instance)]);

        await using var host = await TestDevice.StartAsync(server, forceRelay: false, ServiceModules(gateway));
        await using var controller = await TestDevice.StartAsync(server);
        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.View, Ct);

        // The service refuses the channel itself, so the agent is never even asked.
        await using var sender = await InputSender.OpenAsync(session.Context.Connection, Ct);
        sender.Send(new KeyEvent(0x1E, false, IsDown: true));
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);

        Assert.Empty(injector.Events);
        Assert.Equal(1, gateway.Generations);
    }

    /// <summary>
    /// The service refuses a watch-mode input channel before asking the agent; this bypasses the service-side
    /// dispatcher and asks the agent directly, as a regression there would.
    /// </summary>
    [Fact]
    public async Task The_agent_refuses_a_channel_the_grant_does_not_allow()
    {
        using var injector = new RecordingInjector();
        using var gateway = new InProcessAgentGateway([new InputChannelModule(injector, NullLogger<InputChannelModule>.Instance)]);

        var watch = AccessGrant.Create(SessionMode.View, Permissions.All, Permissions.All);
        await Assert.ThrowsAsync<AgentRefusedException>(
            () => gateway.OpenChannelAsync(AgentChannelKind.Input, SessionId.New(), watch, Ct));
        Assert.Empty(injector.Events);

        // The same request is served when the grant allows it, so the refusal is about the permission and not the channel.
        await using var stream = await gateway.OpenChannelAsync(
            AgentChannelKind.Input,
            SessionId.New(),
            AccessGrant.Create(SessionMode.Control, Permissions.All, Permissions.All),
            Ct);
        await stream.WriteAsync(new byte[] { 0, 1, 2 }, Ct);
        await stream.FlushAsync(Ct);
    }

    private static IFeatureModule[] ServiceModules(IAgentGateway gateway) =>
    [
        new AgentForwardingModule(ChannelType.Video, Permissions.ViewScreen, AgentChannelKind.Video, replayFirstFrame: true, gateway, NullLogger<AgentForwardingModule>.Instance),
        new AgentForwardingModule(ChannelType.Input, Permissions.ControlInput, AgentChannelKind.Input, replayFirstFrame: false, gateway, NullLogger<AgentForwardingModule>.Instance),
    ];

    private static Task WaitForAsync(Func<bool> condition, string what) =>
        TestWait.UntilAsync(condition, what, TimeSpan.FromSeconds(30));
}
