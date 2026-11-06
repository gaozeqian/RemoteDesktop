using Google.Protobuf;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Protocol.Desktop;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// <see cref="DesktopViewer"/> on top of the behaviour both viewers share (<see cref="ViewerDecoderTests"/>): its
/// pointer shape check, and its key frame watchdog with the "stalled" verdict, which the camera viewer does not have.
/// </summary>
public sealed class DesktopViewerDecoderTests : ViewerDecoderTests
{
    public static TheoryData<uint, uint, int, bool> CursorShapes => new()
    {
        { 1, 1, 4, true },
        { 512, 512, 512 * 512 * 4, true },
        { 513, 1, 513 * 4, false },
        { 1, 513, 513 * 4, false },
        { 0, 1, 0, false },
        { 1, 0, 0, false },
        { 2, 2, 15, false },
        { 2, 2, 17, false },
        { 2, 2, 0, false },
    };

    protected override async Task<ViewerRig> StartRigAsync() => await DesktopViewerRig.StartAsync();

    [Theory]
    [MemberData(nameof(CursorShapes))]
    public async Task A_pointer_shape_is_passed_on_only_when_its_size_matches_its_pixels(uint width, uint height, int pixelBytes, bool accepted)
    {
        await using var rig = await DesktopViewerRig.StartAsync();
        var shapes = new List<CursorShape>();
        var moved = false;
        rig.Viewer.CursorShapeChanged += (_, shape) => shapes.Add(shape);
        rig.Viewer.CursorMoved += (_, _) => moved = true;

        await rig.SendControlAsync(new VideoControl
        {
            CursorShape = new CursorShapeUpdate { Width = width, Height = height, Bgra = ByteString.CopyFrom(new byte[pixelBytes]) },
        });

        // Messages are handled in order: once the position that follows has arrived, the shape has been judged.
        await rig.SendControlAsync(new VideoControl { CursorPosition = new CursorPositionUpdate { X = 1, Y = 2, Visible = true } });
        Assert.True(await ViewerRig.WaitUntilAsync(() => moved), "the pointer position that follows arrived");

        Assert.Equal(accepted ? 1 : 0, shapes.Count);
        Assert.False(rig.Closed);
        if (accepted)
        {
            Assert.Equal((int)width, shapes[0].Width);
            Assert.Equal((int)height, shapes[0].Height);
        }
    }

    [Fact]
    public async Task The_watchdog_does_not_ask_for_a_key_frame_while_the_stream_is_young()
    {
        await using var rig = await DesktopViewerRig.StartAsync();

        // Waiting for a key frame, but the stream began a moment ago: not yet a reason to ask.
        await rig.TickAsync(TimeSpan.FromSeconds(1));
        await rig.TickAsync(TimeSpan.FromSeconds(1));

        await rig.ExpectKeyFrameRequestsAsync(0);
    }

    [Fact]
    public async Task The_watchdog_asks_for_a_key_frame_once_nothing_decodable_has_arrived_for_the_stall_timeout_and_not_again_at_once()
    {
        await using var rig = await DesktopViewerRig.StartAsync();
        await Task.Delay(PastStallTimeout, Ct);

        await rig.TickAsync(TimeSpan.FromSeconds(1));
        Assert.True(await ViewerRig.WaitUntilAsync(() => rig.KeyFrameRequests == 1), "the first pass asks");

        // The retry rate while no picture can be shown is one request per two seconds of real time.
        await rig.TickAsync(TimeSpan.FromSeconds(1));
        await rig.TickAsync(TimeSpan.FromSeconds(1));
        await rig.ExpectKeyFrameRequestsAsync(1);
    }

    [Fact]
    public async Task The_watchdog_does_not_ask_before_the_host_has_described_a_stream()
    {
        await using var rig = await DesktopViewerRig.StartAsync(sendStreamInfo: false);
        await Task.Delay(PastStallTimeout, Ct);

        await rig.TickAsync(TimeSpan.FromSeconds(1));

        await rig.ExpectKeyFrameRequestsAsync(0);
    }

    [Fact]
    public async Task A_picture_that_stays_unanswered_by_the_host_is_flagged_as_stalled_until_the_next_picture()
    {
        await using var rig = await DesktopViewerRig.StartAsync();
        rig.Factory.ProducePictures = true;
        await rig.SendKeyFrameAsync();
        Assert.True(await ViewerRig.WaitUntilAsync(() => rig.FrameNumber == 1), "a picture is showing");
        Assert.False(rig.Viewer.IsStalled);

        // Two seconds without a picture: the watchdog asks the host for one. That alone is not a stall.
        await Task.Delay(PastStallTimeout, Ct);
        await rig.TickAsync(TimeSpan.FromSeconds(1));
        Assert.True(await ViewerRig.WaitUntilAsync(() => rig.KeyFrameRequests == 1), "the watchdog asked");
        Assert.False(rig.Viewer.IsStalled);

        // Four seconds on, still unanswered: what is on screen is old, not just unchanged. It asks again, too.
        await Task.Delay(TimeSpan.FromMilliseconds(4200), Ct);
        await rig.TickAsync(TimeSpan.FromSeconds(1));
        Assert.True(await ViewerRig.WaitUntilAsync(() => rig.KeyFrameRequests == 2), "the watchdog asked again");
        Assert.True(rig.Viewer.IsStalled);

        // Any picture at all proves the video path is alive.
        await rig.SendDeltaFramesAsync(1);
        Assert.True(await ViewerRig.WaitUntilAsync(() => rig.FrameNumber == 2), "the host answered");
        Assert.True(await ViewerRig.WaitUntilAsync(() => !rig.Viewer.IsStalled), "the stall is over");
    }
}
