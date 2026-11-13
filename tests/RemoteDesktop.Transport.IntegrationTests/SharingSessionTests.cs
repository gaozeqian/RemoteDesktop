using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Agent;
using RemoteDesktop.Abstractions.Clipboard;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Protocol;
using RemoteDesktop.TestSupport;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>Clipboard and audio sharing over real sessions, directly and through the host service's agent relay.</summary>
public sealed class SharingSessionTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Clipboards_follow_each_other_in_both_directions()
    {
        var hostClipboard = new FakeClipboard();
        await using var host = await TestDevice.StartAsync(server, forceRelay: false, new ClipboardChannelModule(hostClipboard, NullLoggerFactory.Instance));
        await using var controller = await TestDevice.StartAsync(server);
        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);
        var controllerClipboard = new FakeClipboard();
        await using var running = await StartClipboardAsync(session, controllerClipboard);

        controllerClipboard.Copy(new TextClipboardContent("from the controller"));
        await hostClipboard.WaitForAsync(c => c is TextClipboardContent { Text: "from the controller" });

        hostClipboard.Copy(new TextClipboardContent("from the host"));
        await controllerClipboard.WaitForAsync(c => c is TextClipboardContent { Text: "from the host" });
    }

    [Fact]
    public async Task Watch_mode_shares_no_clipboard()
    {
        var hostClipboard = new FakeClipboard();
        await using var host = await TestDevice.StartAsync(server, forceRelay: false, new ClipboardChannelModule(hostClipboard, NullLoggerFactory.Instance));
        await using var controller = await TestDevice.StartAsync(server);
        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.View, Ct);
        Assert.False(session.Context.Grant.Allows(Permissions.Clipboard));

        // A modified controller opens the channel anyway. The host answers by closing it, which the controller sees
        // as the end of the stream: that is proof the refusal was processed, where a fixed pause only proves that
        // nothing had happened yet.
        await using var channel = await session.Context.Connection.OpenChannelAsync(ChannelType.Clipboard, bidirectional: true, Ct);

        // Pushing the secret is allowed to be refused outright, because the host may close the stream before the
        // write lands. What must never happen is the payload being applied, which the assertion below covers.
        try
        {
            await channel.Stream.WriteAsync("secret from the controller"u8.ToArray(), Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Refused before the write; the read that follows is what shows the refusal.
        }

        var probe = new byte[1];
        var read = await channel.Stream.ReadAsync(probe, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(0, read);
        Assert.Equal(0, hostClipboard.SetCalls);
    }

    [Fact]
    public async Task Audio_reaches_the_controller_even_in_watch_mode()
    {
        var players = new RecordingPlayerFactory();
        await using var host = await TestDevice.StartAsync(server, forceRelay: false,
            new AudioChannelModule(new FakeAudioCapturerFactory(), NullLogger<AudioChannelModule>.Instance));
        await using var controller = await TestDevice.StartAsync(server);
        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.View, Ct);
        Assert.True(session.Context.Grant.Allows(Permissions.Audio));

        await using var playback = await AudioPlayback.StartAsync(session.Context.Connection, players, NullLogger.Instance, Ct);

        await WaitForAsync(() => players.Player.SampleCount >= 48_000 * 2 / 2, "half a second of audio");
        Assert.InRange(players.Player.Rms(), 0.2, 0.33); // 0.4 amplitude sine → RMS 0.28

        // Muted audio is dropped on arrival, so unmuting continues live.
        playback.Muted = true;
        await Task.Delay(300, Ct);
        var muted = players.Player.SampleCount;
        await Task.Delay(500, Ct);
        Assert.Equal(muted, players.Player.SampleCount);

        // Silence on its own is also what a dead stream looks like — the whole session, the channel or the decoder
        // could have stopped and the assertion above would still hold. Audio coming back is what tells the two apart.
        playback.Muted = false;
        await WaitForAsync(() => players.Player.SampleCount > muted, "the audio to resume after unmuting");
    }

    [Fact]
    public async Task Clipboard_and_audio_also_work_through_the_service_relay_including_large_pictures()
    {
        var agentClipboard = new FakeClipboard();
        using var gateway = new InProcessAgentGateway(
        [
            new ClipboardChannelModule(agentClipboard, NullLoggerFactory.Instance),
            new AudioChannelModule(new FakeAudioCapturerFactory(), NullLogger<AudioChannelModule>.Instance),
        ]);
        await using var host = await TestDevice.StartAsync(server, forceRelay: false,
            new AgentForwardingModule(ChannelType.Clipboard, Permissions.Clipboard, AgentChannelKind.Clipboard, false, gateway,
                NullLogger<AgentForwardingModule>.Instance, maxInboundFrame: 16 * 1024 * 1024),
            new AgentForwardingModule(ChannelType.Audio, Permissions.Audio, AgentChannelKind.Audio, true, gateway,
                NullLogger<AgentForwardingModule>.Instance));
        await using var controller = await TestDevice.StartAsync(server);
        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);

        var controllerClipboard = new FakeClipboard();
        await using var running = await StartClipboardAsync(session, controllerClipboard);

        // 300 KB: far above the 64 KB that other channels are limited to.
        var picture = new byte[300 * 1024];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(picture, 0);
        new Random(7).NextBytes(picture.AsSpan(8));
        controllerClipboard.Copy(new ImageClipboardContent(picture));
        await agentClipboard.WaitForAsync(c => c is ImageClipboardContent image && image.Png.Span.SequenceEqual(picture));

        var players = new RecordingPlayerFactory();
        await using var playback = await AudioPlayback.StartAsync(session.Context.Connection, players, NullLogger.Instance, Ct);
        await WaitForAsync(() => players.Player.SampleCount >= 48_000, "audio through the relay");
    }

    private static async Task<RunningClipboard> StartClipboardAsync(RemoteDesktop.Application.Sessions.RemoteSession session, FakeClipboard clipboard)
    {
        var channel = await session.Context.Connection.OpenChannelAsync(ChannelType.Clipboard, bidirectional: true, Ct);
        return new RunningClipboard(channel, new ClipboardSynchronizer(clipboard, NullLogger<ClipboardSynchronizer>.Instance));
    }

    private static Task WaitForAsync(Func<bool> condition, string what) =>
        TestWait.UntilAsync(condition, what, TimeSpan.FromSeconds(30));

    private sealed class RunningClipboard : IAsyncDisposable
    {
        private readonly IChannel _channel;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _run;

        public RunningClipboard(IChannel channel, ClipboardSynchronizer synchronizer)
        {
            _channel = channel;
            _run = synchronizer.RunAsync(channel.Stream, _stop.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _channel.DisposeAsync();
            await _run.WaitAsync(TimeSpan.FromSeconds(10));
            _stop.Dispose();
        }
    }
}
