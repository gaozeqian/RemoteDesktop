using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Media;
using RemoteDesktop.Platform.Windows.MediaFoundation;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// The desktop feature end to end over a real session: synthetic captured frames → real Media Foundation encoder →
/// QUIC → real decoder on the controller; input from the controller back to a recording injector.
/// </summary>
public sealed class DesktopSessionTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Controller_receives_decoded_frames_of_the_host_display()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation codecs are Windows only.");
        var codecs = CreateCodecs();
        Assert.SkipUnless(codecs.GetDecoderCandidates(VideoCodec.H264).Any(), "No H.264 decoder.");

        await using var host = await TestDevice.StartAsync(server, forceRelay: false, CreateVideoModule(codecs));
        await using var controller = await TestDevice.StartAsync(server);
        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.View, Ct);

        var frames = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = 0;
        await using var viewer = await DesktopViewer.StartAsync(session.Context.Connection, codecs, 30, TimeProvider.System, NullLogger.Instance, Ct);
        viewer.FrameReady += (_, _) =>
        {
            if (Interlocked.Increment(ref received) >= 10)
            {
                frames.TrySetResult();
            }
        };

        await frames.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.Equal(new DesktopStreamInfo(FakeDisplays.Display.Id, TestScreen.Width, TestScreen.Height, viewer.StreamInfo!.EncoderName, viewer.StreamInfo.HardwareEncoder, viewer.StreamInfo.DecoderName), viewer.StreamInfo);
        var bgra = new byte[TestScreen.Width * TestScreen.Height * 4];
        Assert.True(viewer.CopyLatestFrame(bgra, TestScreen.Width * 4, TestScreen.Width, TestScreen.Height) > 0);

        // The synthetic frame's left half is red, the right half blue (BGRA order).
        var left = ((TestScreen.Height / 2 * TestScreen.Width) + (TestScreen.Width / 4)) * 4;
        var right = ((TestScreen.Height / 2 * TestScreen.Width) + (TestScreen.Width * 3 / 4)) * 4;
        Assert.InRange(bgra[left + 2], 180, 255);
        Assert.InRange(bgra[right + 0], 180, 255);
        Assert.InRange(bgra[right + 2], 0, 80);
    }

    [Fact]
    public async Task Static_screen_still_answers_key_frame_requests()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation codecs are Windows only.");
        var codecs = CreateCodecs();
        Assert.SkipUnless(codecs.GetDecoderCandidates(VideoCodec.H264).Any(), "No H.264 decoder.");

        // One image, then nothing ever changes again.
        var module = new VideoChannelModule(new FakeDisplays(), new FakeCapturerFactory(maxFrames: 1), codecs, TimeProvider.System, NullLogger<VideoChannelModule>.Instance);
        await using var host = await TestDevice.StartAsync(server, forceRelay: false, module);
        await using var controller = await TestDevice.StartAsync(server);
        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.View, Ct);

        var pictures = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var viewer = await DesktopViewer.StartAsync(session.Context.Connection, codecs, 30, TimeProvider.System, NullLogger.Instance, Ct);
        viewer.FrameReady += (_, _) => (Interlocked.Increment(ref pictures) == 1 ? first : second).TrySetResult();

        await first.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await viewer.RequestKeyFrameAsync(Ct);
        await second.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task Input_reaches_the_host_injector_and_held_keys_are_released_on_disconnect()
    {
        using var injector = new RecordingInjector();
        await using var host = await TestDevice.StartAsync(server, forceRelay: false, new InputChannelModule(injector, NullLogger<InputChannelModule>.Instance));
        await using var controller = await TestDevice.StartAsync(server);
        var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);

        var sender = await InputSender.OpenAsync(session.Context.Connection, Ct);
        sender.Send(new PointerMoveEvent("d", 0.5, 0.5));
        sender.Send(new KeyEvent(0x1D, false, IsDown: true)); // Ctrl held...
        await injector.WaitForAsync(2);

        // ...when the connection drops: the host must release it.
        await sender.DisposeAsync();
        await session.DisposeAsync();
        await injector.WaitForAsync(3);

        Assert.Equal(new PointerMoveEvent("d", 0.5, 0.5), injector.Events[0]);
        Assert.Equal(new KeyEvent(0x1D, false, true), injector.Events[1]);
        Assert.Equal(new KeyEvent(0x1D, false, false), injector.Events[2]);
    }

    [Fact]
    public async Task Watch_mode_input_never_reaches_the_injector()
    {
        using var injector = new RecordingInjector();
        await using var host = await TestDevice.StartAsync(server, forceRelay: false, new InputChannelModule(injector, NullLogger<InputChannelModule>.Instance));
        await using var controller = await TestDevice.StartAsync(server);
        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.View, Ct);

        // A modified controller could still try; the host refuses the channel.
        await using var sender = await InputSender.OpenAsync(session.Context.Connection, Ct);
        sender.Send(new KeyEvent(0x1E, false, true));
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);

        Assert.Empty(injector.Events);
    }

    private static VideoCodecSelector CreateCodecs() =>
        new([new MediaFoundationH264EncoderFactory(hardware: false)], [new MediaFoundationH264DecoderFactory()]);

    private static VideoChannelModule CreateVideoModule(VideoCodecSelector codecs) =>
        new(new FakeDisplays(), new FakeCapturerFactory(), codecs, TimeProvider.System, NullLogger<VideoChannelModule>.Instance);

}
