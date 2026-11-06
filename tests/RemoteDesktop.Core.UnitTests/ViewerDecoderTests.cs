using RemoteDesktop.Media;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// What a controller-side viewer (desktop or camera) does with a decoder that keeps being fed and stops producing
/// pictures, and with the stream descriptions it is sent. The host is played by the other end of a pipe and the
/// decoder by a fake that never has a picture; what is asserted is when the viewer gives up on that decoder, what it
/// asks the host for, and which streams it refuses. Both viewers share this behaviour, so both run exactly these tests.
/// </summary>
public abstract class ViewerDecoderTests
{
    // The viewers measure the stall timeout (two seconds) with the monotonic clock, which a test cannot move; the tests
    // that need it wait it out in real time.
    protected static readonly TimeSpan PastStallTimeout = TimeSpan.FromMilliseconds(2300);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<uint, uint> RefusedSizes => new()
    {
        { 0, 0 },
        { 1, 2 },
        { 2, 1 },
        { 8193, 2 },
        { 2, 8193 },
        { 8192, 4051 },
        { 7681, 4321 },
    };

    public static TheoryData<uint, uint> AcceptedSizes => new()
    {
        { 2, 2 },
        { 8192, 2 },
        { 2, 8192 },
    };

    protected abstract Task<ViewerRig> StartRigAsync();

    [Fact]
    public async Task A_run_of_silent_non_key_frames_rebuilds_the_decoder()
    {
        await using var rig = await StartRigAsync();
        await rig.SendKeyFrameAsync();
        await rig.WaitForDecodesAsync(1);
        await Task.Delay(PastStallTimeout, Ct);

        await rig.SendDeltaFramesAsync(2);
        await rig.WaitForDecodesAsync(3);
        Assert.Equal(1, rig.Factory.Created);

        await rig.SendDeltaFramesAsync(1);
        Assert.True(await ViewerRig.WaitUntilAsync(() => rig.Factory.Created == 2), "the third silent frame in a row rebuilds the decoder");
        Assert.True(rig.Factory.Decoders[0].Disposed, "the decoder that lost sync is released");
    }

    [Fact]
    public async Task A_key_frame_the_decoder_has_nothing_for_does_not_count_towards_a_rebuild()
    {
        await using var rig = await StartRigAsync();
        await Task.Delay(PastStallTimeout, Ct);

        // Parameter sets on their own give no picture; throwing the decoder away over them would lose what it learned.
        for (var i = 0; i < 6; i++)
        {
            await rig.SendKeyFrameAsync();
        }

        await rig.WaitForDecodesAsync(6);
        await Task.Delay(200, Ct);
        Assert.Equal(1, rig.Factory.Created);
    }

    [Fact]
    public async Task A_decoder_that_cannot_be_rebuilt_leaves_the_old_one_in_place_and_the_stream_open()
    {
        await using var rig = await StartRigAsync();
        await rig.SendKeyFrameAsync();
        await rig.WaitForDecodesAsync(1);
        await Task.Delay(PastStallTimeout, Ct);

        rig.Factory.FailNextCreate = true;
        await rig.SendDeltaFramesAsync(3);
        await rig.WaitForDecodesAsync(4);
        Assert.True(await ViewerRig.WaitUntilAsync(() => rig.Factory.FailedCreates == 1), "a rebuild was attempted");

        // The viewer asked for a key frame and is waiting for it; the old decoder must still be there to take it.
        await rig.SendKeyFrameAsync();
        await rig.WaitForDecodesAsync(5);
        Assert.False(rig.Factory.Decoders[0].Disposed);
        Assert.False(rig.Closed);
    }

    [Fact]
    public async Task A_decoder_that_cannot_be_rebuilt_still_has_the_host_asked_for_a_key_frame()
    {
        await using var rig = await StartRigAsync();
        await rig.SendKeyFrameAsync();
        await rig.WaitForDecodesAsync(1);
        await Task.Delay(PastStallTimeout, Ct);

        rig.Factory.FailNextCreate = true;
        await rig.SendDeltaFramesAsync(3);
        Assert.True(await ViewerRig.WaitUntilAsync(() => rig.Factory.FailedCreates == 1), "a rebuild was attempted");

        Assert.True(await ViewerRig.WaitUntilAsync(() => rig.KeyFrameRequests == 1), "the host is asked for a key frame");
        Assert.Equal(1, rig.Factory.Created);
    }

    [Fact]
    public async Task Silent_frames_before_the_stall_timeout_do_not_rebuild_the_decoder()
    {
        await using var rig = await StartRigAsync();

        // The stream is young: however many quiet answers come, the decoder has not been fed for long enough to judge.
        await rig.SendKeyFrameAsync();
        await rig.SendDeltaFramesAsync(5);
        await rig.WaitForDecodesAsync(6);

        Assert.Equal(1, rig.Factory.Created);
        await rig.ExpectKeyFrameRequestsAsync(0);
    }

    [Fact]
    public async Task A_rebuild_asks_the_host_for_a_key_frame_and_drops_the_frames_until_it_arrives()
    {
        await using var rig = await StartRigAsync();
        await rig.SendKeyFrameAsync();
        await rig.WaitForDecodesAsync(1);
        await Task.Delay(PastStallTimeout, Ct);

        await rig.SendDeltaFramesAsync(3);
        Assert.True(await ViewerRig.WaitUntilAsync(() => rig.Factory.Created == 2), "the decoder was rebuilt");
        await rig.ExpectKeyFrameRequestsAsync(1);

        // The new decoder knows nothing of the stream: only a key frame may be fed to it.
        await rig.SendDeltaFramesAsync(2);
        await rig.SendKeyFrameAsync();
        await rig.WaitForDecodesAsync(5);
        Assert.Equal(1, rig.Factory.Decoders[1].Decodes);
        Assert.Equal(4, rig.Factory.Decoders[0].Decodes);
    }

    [Fact]
    public async Task After_a_rebuild_the_stall_clock_starts_over()
    {
        await using var rig = await StartRigAsync();
        await rig.SendKeyFrameAsync();
        await rig.WaitForDecodesAsync(1);
        await Task.Delay(PastStallTimeout, Ct);
        await rig.SendDeltaFramesAsync(3);
        Assert.True(await ViewerRig.WaitUntilAsync(() => rig.Factory.Created == 2), "the decoder was rebuilt");

        // Silent again right away: a run of three, but the rebuild is not two seconds old, so there is no second one.
        await rig.SendKeyFrameAsync();
        await rig.SendDeltaFramesAsync(5);
        await rig.WaitForDecodesAsync(10);

        Assert.Equal(2, rig.Factory.Created);
        Assert.Equal(6, rig.Factory.Decoders[1].Decodes);
    }

    [Fact]
    public async Task A_picture_restarts_the_stall_clock()
    {
        await using var rig = await StartRigAsync();
        await rig.SendKeyFrameAsync();
        await rig.WaitForDecodesAsync(1);
        await Task.Delay(PastStallTimeout, Ct);
        await rig.SendDeltaFramesAsync(2);
        await rig.WaitForDecodesAsync(3);

        rig.Factory.ProducePictures = true;
        await rig.SendDeltaFramesAsync(1);
        Assert.True(await ViewerRig.WaitUntilAsync(() => rig.FrameNumber == 1), "a picture came out");

        // Silent again: a run longer than the limit, but the decoder showed a picture a moment ago.
        rig.Factory.ProducePictures = false;
        await rig.SendDeltaFramesAsync(5);
        await rig.WaitForDecodesAsync(9);

        Assert.Equal(1, rig.Factory.Created);
    }

    [Fact]
    public async Task A_malformed_video_frame_is_dropped_and_the_stream_goes_on()
    {
        await using var rig = await StartRigAsync();
        rig.Factory.ProducePictures = true;

        // Shorter than the video frame header: Parse refuses it. That costs one frame, not the whole viewer.
        await rig.SendRawVideoFrameAsync(new byte[3]);
        await rig.SendKeyFrameAsync();

        Assert.True(await ViewerRig.WaitUntilAsync(() => rig.FrameNumber >= 1), "a picture still arrives after the bad frame");
        Assert.False(rig.Closed);
    }

    [Fact]
    public async Task A_malformed_video_frame_asks_the_host_for_a_key_frame_and_waits_for_it()
    {
        await using var rig = await StartRigAsync();

        await rig.SendRawVideoFrameAsync(new byte[3]);
        Assert.True(await ViewerRig.WaitUntilAsync(() => rig.KeyFrameRequests == 1), "the host is asked for a key frame");

        // Waiting for it: a delta frame is not fed to the decoder, the key frame is.
        await rig.SendDeltaFramesAsync(1);
        await rig.SendKeyFrameAsync();
        await rig.WaitForDecodesAsync(1);
        Assert.Equal(1, rig.Factory.Decoders[0].Decodes);
        await rig.ExpectKeyFrameRequestsAsync(1);
    }

    [Fact]
    public async Task A_stream_description_that_cannot_be_used_still_ends_the_viewer()
    {
        await using var rig = await StartRigAsync();

        await rig.SendStreamInfoAsync(width: 0, height: 0);

        Assert.True(await ViewerRig.WaitUntilAsync(() => rig.Closed), "an unusable stream description cannot be recovered from");
    }

    [Theory]
    [MemberData(nameof(RefusedSizes))]
    public async Task A_stream_size_outside_the_limits_ends_the_viewer_without_a_new_decoder(uint width, uint height)
    {
        await using var rig = await StartRigAsync();

        await rig.SendStreamInfoAsync(width, height);

        Assert.True(await ViewerRig.WaitUntilAsync(() => rig.Closed), $"{width}x{height} is refused");
        Assert.Equal(1, rig.Factory.Created);
        Assert.False(rig.Factory.Decoders[0].Disposed);
    }

    [Theory]
    [MemberData(nameof(AcceptedSizes))]
    public async Task A_stream_size_at_the_limits_is_accepted(uint width, uint height)
    {
        await using var rig = await StartRigAsync();

        await rig.SendStreamInfoAsync(width, height);

        Assert.True(await ViewerRig.WaitUntilAsync(() => rig.StreamWidth == width), $"{width}x{height} is taken");
        Assert.False(rig.Closed);
        Assert.Equal(2, rig.Factory.Created);
    }

    [Fact]
    public async Task A_new_stream_description_replaces_the_decoder_even_at_the_same_size()
    {
        await using var rig = await StartRigAsync();
        await rig.SendKeyFrameAsync();
        await rig.WaitForDecodesAsync(1);

        await rig.SendStreamInfoAsync(64, 64);
        Assert.True(await ViewerRig.WaitUntilAsync(() => rig.Factory.Created == 2), "a fresh decoder for the fresh encoder");
        Assert.True(rig.Factory.Decoders[0].Disposed, "the decoder of the old stream is released");

        // The new stream starts over: only its key frame is fed to the new decoder.
        await rig.SendDeltaFramesAsync(1);
        await rig.SendKeyFrameAsync();
        await rig.WaitForDecodesAsync(2);
        Assert.Equal(1, rig.Factory.Decoders[1].Decodes);
        Assert.Equal(1, rig.Factory.Decoders[0].Decodes);
    }

    [Fact]
    public async Task Disposing_the_viewer_releases_its_decoder()
    {
        var rig = await StartRigAsync();
        var decoder = rig.Factory.Decoders[0];
        Assert.False(decoder.Disposed);

        await rig.DisposeAsync();

        Assert.True(decoder.Disposed);
    }

    [Fact]
    public async Task A_watchdog_pass_does_not_outlive_the_viewer()
    {
        var rig = await StartRigAsync();
        Assert.Equal(1, rig.Time.PendingTimers);

        await rig.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(0, rig.Time.PendingTimers);
    }
}
