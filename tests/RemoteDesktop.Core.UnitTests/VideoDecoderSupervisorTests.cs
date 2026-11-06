using RemoteDesktop.Media;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The judgement both viewers share about a decoder that has lost sync, tested on its own with fake decoders and a
/// clock the test moves: the viewer-level behaviour is in <see cref="ViewerDecoderTests"/>.
/// </summary>
public sealed class VideoDecoderSupervisorTests
{
    private static readonly TimeSpan JustPastStall = VideoDecoderSupervisor.StallTimeout + TimeSpan.FromMilliseconds(1);

    public static TheoryData<int, int> RefusedSizes => new()
    {
        { 0, 0 },
        { -1, 100 },
        { 1, 2 },
        { 2, 1 },
        { 8193, 2 },
        { 2, 8193 },
        { 8192, 4051 },
        { 7681, 4321 },
        { 16384, 16384 },
    };

    public static TheoryData<int, int> AcceptedSizes => new()
    {
        { 2, 2 },
        { 8192, 2 },
        { 2, 8192 },
        { 7680, 4320 },
        { 8192, 4050 },
        { 1920, 1080 },
    };

    [Fact]
    public void There_is_no_decoder_before_a_stream_has_started()
    {
        var rig = new Rig();

        Assert.Null(rig.Supervisor.Decoder);
        Assert.Equal(0, rig.Factory.Created);
    }

    [Theory]
    [MemberData(nameof(AcceptedSizes))]
    public void A_stream_of_acceptable_size_gets_a_decoder(int width, int height)
    {
        var rig = new Rig();

        Assert.True(VideoDecoderSupervisor.IsAcceptableSize(width, height));
        rig.Supervisor.Start(width, height);

        Assert.Same(rig.Factory.Decoders[0], rig.Supervisor.Decoder);
    }

    [Theory]
    [MemberData(nameof(RefusedSizes))]
    public void A_stream_of_unacceptable_size_is_refused_and_changes_nothing(int width, int height)
    {
        var rig = new Rig();
        rig.Supervisor.Start(64, 64);

        Assert.False(VideoDecoderSupervisor.IsAcceptableSize(width, height));
        Assert.Throws<InvalidDataException>(() => rig.Supervisor.Start(width, height));

        Assert.Equal(1, rig.Factory.Created);
        Assert.Same(rig.Factory.Decoders[0], rig.Supervisor.Decoder);
        Assert.False(rig.Factory.Decoders[0].Disposed);
    }

    [Fact]
    public void Starting_a_new_stream_replaces_the_decoder_and_forgets_the_silent_run()
    {
        var rig = new Rig();
        rig.Supervisor.Start(64, 64);
        rig.Clock.Advance(JustPastStall);
        Assert.False(rig.Supervisor.RecordSilentFrame(false));
        Assert.False(rig.Supervisor.RecordSilentFrame(false));

        rig.Supervisor.Start(64, 64);

        Assert.Equal(2, rig.Factory.Created);
        Assert.True(rig.Factory.Decoders[0].Disposed);
        Assert.Same(rig.Factory.Decoders[1], rig.Supervisor.Decoder);

        // Start counts from nothing: the stall clock was restarted too, so even a long run is not out of sync yet.
        Assert.False(rig.Supervisor.RecordSilentFrame(false));
        Assert.False(rig.Supervisor.RecordSilentFrame(false));
        Assert.False(rig.Supervisor.RecordSilentFrame(false));
        rig.Clock.Advance(JustPastStall);
        Assert.True(rig.Supervisor.RecordSilentFrame(false));
    }

    [Fact]
    public void A_run_of_silent_non_key_frames_is_out_of_sync_only_once_the_stall_timeout_has_passed()
    {
        var rig = new Rig();
        rig.Supervisor.Start(64, 64);

        for (var i = 0; i < 10; i++)
        {
            Assert.False(rig.Supervisor.RecordSilentFrame(false), "the stream is young");
        }

        // Just short of the timeout, then exactly at it: the timeout is inclusive.
        rig.Clock.Advance(VideoDecoderSupervisor.StallTimeout - TimeSpan.FromTicks(1));
        Assert.False(rig.Supervisor.RecordSilentFrame(false));
        rig.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(rig.Supervisor.RecordSilentFrame(false));
    }

    [Fact]
    public void The_silent_run_must_reach_the_limit_even_when_the_stall_timeout_has_long_passed()
    {
        var rig = new Rig();
        rig.Supervisor.Start(64, 64);
        rig.Clock.Advance(TimeSpan.FromMinutes(5));

        for (var i = 1; i < VideoDecoderSupervisor.SilentFramesBeforeRebuild; i++)
        {
            Assert.False(rig.Supervisor.RecordSilentFrame(false), $"{i} silent frame(s) are not yet a run");
        }

        Assert.True(rig.Supervisor.RecordSilentFrame(false));
    }

    [Fact]
    public void A_key_frame_neither_counts_nor_breaks_the_run()
    {
        var rig = new Rig();
        rig.Supervisor.Start(64, 64);
        rig.Clock.Advance(JustPastStall);

        Assert.False(rig.Supervisor.RecordSilentFrame(false));
        Assert.False(rig.Supervisor.RecordSilentFrame(false));
        Assert.False(rig.Supervisor.RecordSilentFrame(true), "parameter sets alone are an ordinary quiet answer");
        Assert.False(rig.Supervisor.RecordSilentFrame(true));
        Assert.True(rig.Supervisor.RecordSilentFrame(false), "the third non-key frame completes the run");
    }

    [Fact]
    public void A_picture_clears_the_run_and_restarts_the_stall_clock()
    {
        var rig = new Rig();
        rig.Supervisor.Start(64, 64);
        rig.Clock.Advance(JustPastStall);
        Assert.False(rig.Supervisor.RecordSilentFrame(false));
        Assert.False(rig.Supervisor.RecordSilentFrame(false));

        rig.Supervisor.PictureShown();

        // Neither the count (two before) nor the time (past the timeout before) survives a picture.
        for (var i = 0; i < 5; i++)
        {
            Assert.False(rig.Supervisor.RecordSilentFrame(false));
        }

        rig.Clock.Advance(JustPastStall);
        Assert.True(rig.Supervisor.RecordSilentFrame(false));
    }

    [Fact]
    public void The_quiet_time_runs_from_the_last_picture_or_the_start_of_the_stream()
    {
        var rig = new Rig();
        rig.Supervisor.Start(64, 64);

        rig.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(3), rig.Supervisor.QuietFor(rig.Clock.GetTimestamp()));

        rig.Supervisor.PictureShown();
        rig.Clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(TimeSpan.FromMilliseconds(500), rig.Supervisor.QuietFor(rig.Clock.GetTimestamp()));
    }

    [Fact]
    public void A_rebuild_swaps_in_a_decoder_for_the_same_stream_and_releases_the_old_one()
    {
        var rig = new Rig();
        rig.Supervisor.Start(64, 48);
        var old = rig.Supervisor.Decoder;

        rig.Supervisor.Rebuild();

        Assert.Equal(2, rig.Factory.Created);
        Assert.Equal((64, 48), rig.Factory.Sizes[1]);
        Assert.NotSame(old, rig.Supervisor.Decoder);
        Assert.Same(rig.Factory.Decoders[1], rig.Supervisor.Decoder);
        Assert.True(rig.Factory.Decoders[0].Disposed);
        Assert.False(rig.Factory.Decoders[1].Disposed);
        Assert.Empty(rig.Failures);
    }

    [Fact]
    public void A_rebuild_starts_the_run_and_the_stall_clock_over()
    {
        var rig = new Rig();
        rig.Supervisor.Start(64, 64);
        rig.Clock.Advance(JustPastStall);
        Assert.False(rig.Supervisor.RecordSilentFrame(false));
        Assert.False(rig.Supervisor.RecordSilentFrame(false));
        Assert.True(rig.Supervisor.RecordSilentFrame(false));

        rig.Supervisor.Rebuild();

        for (var i = 0; i < 5; i++)
        {
            Assert.False(rig.Supervisor.RecordSilentFrame(false), "the rebuilt decoder gets its own two seconds");
        }

        rig.Clock.Advance(JustPastStall);
        Assert.True(rig.Supervisor.RecordSilentFrame(false));
    }

    [Fact]
    public void A_rebuild_that_cannot_create_a_decoder_keeps_the_old_one_reports_why_and_still_starts_over()
    {
        var rig = new Rig();
        rig.Supervisor.Start(64, 64);
        rig.Clock.Advance(JustPastStall);
        Assert.False(rig.Supervisor.RecordSilentFrame(false));
        Assert.False(rig.Supervisor.RecordSilentFrame(false));
        Assert.True(rig.Supervisor.RecordSilentFrame(false));
        rig.Factory.FailNextCreate = true;

        rig.Supervisor.Rebuild();

        Assert.Single(rig.Failures);
        Assert.IsType<NotSupportedException>(rig.Failures[0]);
        Assert.Same(rig.Factory.Decoders[0], rig.Supervisor.Decoder);
        Assert.False(rig.Factory.Decoders[0].Disposed);

        // Not retried on the very next frame.
        Assert.False(rig.Supervisor.RecordSilentFrame(false));
        Assert.False(rig.Supervisor.RecordSilentFrame(false));
        rig.Clock.Advance(JustPastStall);
        Assert.True(rig.Supervisor.RecordSilentFrame(false));
    }

    [Fact]
    public void A_rebuild_before_any_stream_has_started_creates_nothing()
    {
        var rig = new Rig();

        rig.Supervisor.Rebuild();

        Assert.Equal(0, rig.Factory.Created);
        Assert.Null(rig.Supervisor.Decoder);
        Assert.Empty(rig.Failures);
    }

    [Fact]
    public void Disposing_releases_the_current_decoder()
    {
        var rig = new Rig();
        rig.Supervisor.Start(64, 64);
        rig.Supervisor.Rebuild();

        rig.Supervisor.Dispose();

        Assert.True(rig.Factory.Decoders[1].Disposed);
    }

    [Fact]
    public void Disposing_before_any_stream_has_started_is_harmless()
    {
        var rig = new Rig();

        rig.Supervisor.Dispose();

        Assert.Equal(0, rig.Factory.Created);
    }

    private sealed class Rig
    {
        public Rig()
        {
            Supervisor = new VideoDecoderSupervisor(new VideoCodecSelector([], [Factory]), Clock, Failures.Add);
        }

        public FakeDecoderFactory Factory { get; } = new();

        public SteppedTime Clock { get; } = new();

        public List<Exception> Failures { get; } = [];

        public VideoDecoderSupervisor Supervisor { get; }
    }
}
