using RemoteDesktop.Features.Camera;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// <see cref="CameraViewer"/> on top of the behaviour both viewers share (<see cref="ViewerDecoderTests"/>): the key
/// frame watchdog it now shares with the desktop viewer, and the refusal of an unusable first stream description.
/// </summary>
public sealed class CameraViewerDecoderTests : ViewerDecoderTests
{
    protected override async Task<ViewerRig> StartRigAsync() => await CameraViewerRig.StartAsync();

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8193, 2)]
    [InlineData(7681, 4321)]
    public async Task A_first_stream_description_outside_the_limits_fails_the_start_without_a_decoder(uint width, uint height)
    {
        var (server, client) = await TestPipe.ConnectAsync(Ct);
        await using var host = client;
        var factory = new FakeDecoderFactory();

        await Assert.ThrowsAsync<CameraException>(() => CameraViewerRig.StartViewerAsync(server, client, factory, TimeProvider.System, width, height));

        Assert.Equal(0, factory.Created);
    }

    [Fact]
    public async Task The_watchdog_asks_for_a_key_frame_once_the_stall_timeout_has_passed()
    {
        await using var rig = await CameraViewerRig.StartAsync();

        // Waiting for a key frame, but the stream began a moment ago: not yet a reason to ask.
        await rig.TickAsync(TimeSpan.FromSeconds(1));
        await rig.ExpectKeyFrameRequestsAsync(0);

        await rig.TickAsync(PastStallTimeout);
        Assert.True(await ViewerRig.WaitUntilAsync(() => rig.KeyFrameRequests == 1), "the first pass asks");

        // The retry rate while no picture can be shown is one request per two seconds of clock time.
        await rig.TickAsync(TimeSpan.FromSeconds(1));
        await rig.ExpectKeyFrameRequestsAsync(1);
    }

    [Fact]
    public async Task A_picture_that_stops_arriving_is_checked_with_one_key_frame_and_no_immediate_repeat()
    {
        await using var rig = await CameraViewerRig.StartAsync();
        rig.Factory.ProducePictures = true;
        await rig.SendKeyFrameAsync();
        Assert.True(await ViewerRig.WaitUntilAsync(() => rig.FrameNumber == 1), "a picture is showing");

        // A static scene produces nothing on its own; after the stall timeout the watchdog asks once to prove the
        // path is alive. The next two seconds back off instead of asking again.
        await rig.TickAsync(TimeSpan.FromSeconds(2));
        Assert.True(await ViewerRig.WaitUntilAsync(() => rig.KeyFrameRequests == 1), "the quiet picture is checked");
        await rig.TickAsync(TimeSpan.FromSeconds(2));

        await rig.ExpectKeyFrameRequestsAsync(1);
    }

    [Fact]
    public async Task The_watchdog_asks_again_after_a_decode_error_has_put_the_viewer_back_to_waiting()
    {
        await using var rig = await CameraViewerRig.StartAsync();
        rig.Factory.ProducePictures = true;
        await rig.SendKeyFrameAsync();
        Assert.True(await ViewerRig.WaitUntilAsync(() => rig.FrameNumber == 1), "a picture is showing");

        await rig.SendRawVideoFrameAsync(new byte[3]);
        Assert.True(await ViewerRig.WaitUntilAsync(() => rig.KeyFrameRequests == 1), "the bad frame is answered with a request");

        // The shared watchdog waits for the stall timeout of silence before asking again, as the desktop one does.
        await rig.TickAsync(PastStallTimeout);
        await rig.ExpectKeyFrameRequestsAsync(2);
    }

    [Fact]
    public void Opening_a_camera_waits_longer_than_a_generic_request()
    {
        // Opening can block on a TCC prompt on the controlled end, which only the person at that machine can answer.
        // The generic 30-second deadline expired while the prompt was still up and turned a denial into a timeout.
        Assert.True(
            CameraViewer.OpenTimeout > RequestTimeout.Default,
            $"Camera open waits {CameraViewer.OpenTimeout}, which is not longer than the generic {RequestTimeout.Default}.");
    }
}
