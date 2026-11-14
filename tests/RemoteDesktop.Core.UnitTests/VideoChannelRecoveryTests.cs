using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Media;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Desktop;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.TestSupport;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The capture loop's recovery gate: a capturer that says it is broken while no frames arrive has to be rebuilt, even
/// though a static screen looks exactly the same from the outside.
/// </summary>
public sealed class VideoChannelRecoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DisplayInfo Display = new(
        "display-1",
        "Test",
        new PixelRect(0, 0, 64, 64),
        1,
        IsPrimary: true);

    [Fact]
    public async Task A_capturer_that_reports_a_fault_without_frames_is_reset()
    {
        var capturer = new FaultingCapturer();
        var module = new VideoChannelModule(
            new SingleDisplay(),
            new FixedCapturerFactory(capturer),
            new VideoCodecSelector([], []),
            TimeProvider.System,
            NullLogger<VideoChannelModule>.Instance);

        var (server, client) = await TestPipe.ConnectAsync(Ct);
        await using var serverEnd = server;
        await using var clientEnd = client;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var session = new SessionContext(
            SessionId.New(),
            SessionRole.Host,
            null!,
            AccessGrant.Create(SessionMode.View, Permissions.ViewScreen, Permissions.ViewScreen));

        var handling = module.HandleAsync(new TestChannel(ChannelType.Video, serverEnd), session, stop.Token);
        await ProtobufFrames.WriteAsync(
            clientEnd,
            new VideoControl { Request = new VideoRequest { DisplayId = Display.Id, MaxFrameRate = 30 } },
            Ct);

        // NoFrameTimeout is two seconds; allow the loop a little longer than that to act.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (capturer.Resets == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        Assert.True(capturer.Resets > 0, "The capture loop did not rebuild a capturer that reported a fault.");

        await stop.CancelAsync();
        await handling;
    }

    private sealed class SingleDisplay : IDisplayEnumerator
    {
        public IReadOnlyList<DisplayInfo> GetDisplays() => [Display];
    }

    private sealed class FixedCapturerFactory(IScreenCapturer capturer) : IScreenCapturerFactory
    {
        public IScreenCapturer Create(DisplayInfo display) => capturer;
    }

    /// <summary>A capturer that is broken from the start and never produces a frame.</summary>
    private sealed class FaultingCapturer : IScreenCapturer
    {
        public DisplayInfo Display => VideoChannelRecoveryTests.Display;

        public bool IsFaulted => true;

        public int Resets { get; private set; }

        // Explicit accessors: this capturer never raises cursor events, and an unused field would be a warning.
        public event EventHandler<CursorShape>? CursorChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<CursorPosition>? CursorMoved
        {
            add { }
            remove { }
        }

        public void Reset() => Resets++;

        public async ValueTask<VideoFrame?> CaptureAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            // The real capture blocks until its timeout; the delay keeps this fake from spinning the loop.
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            return null;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
