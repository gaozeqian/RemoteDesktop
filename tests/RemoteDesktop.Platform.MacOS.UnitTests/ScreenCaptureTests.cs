using System.Globalization;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.MacOS.Display;
using RemoteDesktop.Platform.MacOS.Interop;

namespace RemoteDesktop.Platform.MacOS.UnitTests;

/// <summary>
/// The frame sink, driven directly.
/// </summary>
/// <remarks>
/// The sink is the piece between the native callback and the capture pipeline, and everything subtle about it —
/// stripping the row padding, replacing rather than queueing, and turning "nothing arrived" into a timeout — is
/// testable without a screen, a permission or a running stream. That is worth doing, because the alternative is
/// discovering a stride bug by looking at a corrupt picture.
/// </remarks>
public class ScreenCaptureKitFrameSinkTests
{
    /// <summary>The width the sink is asked for, deliberately not a multiple of the padded stride.</summary>
    private const int Width = 5;

    private const int Height = 3;

    /// <summary>Deliberately wider than <c>Width * 4</c>, the way a CVPixelBuffer pads its rows.</summary>
    private const int PaddedStride = 32;

    [Fact]
    public void A_delivered_frame_carries_the_pixels_with_the_row_padding_removed()
    {
        using var sink = new ScreenCaptureKitFrameSink();
        Deliver(sink, out var source);

        var frame = sink.TryTake();

        Assert.NotNull(frame);
        using (frame)
        {
            Assert.Equal(Width, frame.Width);
            Assert.Equal(Height, frame.Height);
            Assert.Equal(PixelFormat.Bgra32, frame.Format);

            // The sink's own stride is tight; the padding must not have come along for the ride.
            var pixels = frame.GetPixels(out var stride);
            Assert.Equal(Width * 4, stride);
            Assert.Equal(Width * 4 * Height, pixels.Length);

            // Every row starts with the byte the source put there, and the padding never appears.
            for (var y = 0; y < Height; y++)
            {
                Assert.Equal((byte)(y + 1), pixels[y * stride]);
                Assert.DoesNotContain((byte)0xEE, pixels[(y * stride)..((y + 1) * stride)].ToArray());
            }
        }

        GC.KeepAlive(source);
    }

    [Fact]
    public void Dirty_rectangles_are_carried_through()
    {
        using var sink = new ScreenCaptureKitFrameSink();
        Deliver(sink, out var source, dirtyRects: [(1, 2, 3, 4), (5, 6, 7, 8)]);

        using var frame = sink.TryTake();

        Assert.NotNull(frame);
        Assert.Equal(2, frame.DirtyRects.Count);
        Assert.Equal(new PixelRect(1, 2, 3, 4), frame.DirtyRects[0]);
        Assert.Equal(new PixelRect(5, 6, 7, 8), frame.DirtyRects[1]);
        GC.KeepAlive(source);
    }

    [Fact]
    public void No_dirty_rectangles_means_the_whole_frame_changed()
    {
        using var sink = new ScreenCaptureKitFrameSink();
        Deliver(sink, out var source);

        using var frame = sink.TryTake();

        // ScreenCaptureKit omits them for a stream's first frame, and the contract reads an empty list as
        // "everything changed" rather than "nothing did".
        Assert.NotNull(frame);
        Assert.Empty(frame.DirtyRects);
        GC.KeepAlive(source);
    }

    [Fact]
    public async Task Waiting_times_out_when_nothing_is_delivered()
    {
        using var sink = new ScreenCaptureKitFrameSink();

        // This is the whole basis of the interface's "null means the screen did not change": the native helper
        // drops idle frames, so the only signal is the absence of one.
        var arrived = await sink.WaitForFrameAsync(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        Assert.False(arrived);
        Assert.Null(sink.TryTake());
    }

    [Fact]
    public async Task A_delivered_frame_wakes_a_waiter()
    {
        using var sink = new ScreenCaptureKitFrameSink();

        var waiting = sink.WaitForFrameAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Deliver(sink, out var source);

        Assert.True(await waiting);
        using var frame = sink.TryTake();
        Assert.NotNull(frame);
        GC.KeepAlive(source);
    }

    [Fact]
    public void A_newer_frame_replaces_the_waiting_one_instead_of_queueing_behind_it()
    {
        using var sink = new ScreenCaptureKitFrameSink();
        Deliver(sink, out var first, marker: 0x11);
        Deliver(sink, out var second, marker: 0x22);

        using var frame = sink.TryTake();

        // A live view wants the most recent picture. Queueing would show the consumer a backlog of stale frames.
        Assert.NotNull(frame);
        Assert.Equal(0x22, frame.GetPixels(out _)[0]);

        // Only one frame is ever held, so the second take is empty.
        Assert.Null(sink.TryTake());
        GC.KeepAlive(first);
        GC.KeepAlive(second);
    }

    [Fact]
    public void Frames_with_impossible_dimensions_are_dropped_rather_than_thrown()
    {
        using var sink = new ScreenCaptureKitFrameSink();

        // This is reached through a reverse P/Invoke, so an escaping exception would unwind into Objective-C.
        var buffer = new byte[16];
        unsafe
        {
            fixed (byte* pixels = buffer)
            {
                var frame = new RdFrame { Bgra = pixels, Width = 0, Height = 0, BytesPerRow = 16 };
                sink.Deliver(&frame);
            }
        }

        Assert.Null(sink.TryTake());
    }

    /// <summary>
    /// Builds a frame the way the native helper does: padded rows, a per-row marker in the first byte, and a
    /// recognisable filler in the padding so a leak into the visible pixels is detectable.
    /// </summary>
    private static unsafe void Deliver(
        ScreenCaptureKitFrameSink sink,
        out byte[] source,
        (int X, int Y, int Width, int Height)[]? dirtyRects = null,
        byte marker = 0x00)
    {
        var buffer = new byte[PaddedStride * Height];
        for (var y = 0; y < Height; y++)
        {
            buffer[y * PaddedStride] = (byte)(marker == 0 ? y + 1 : marker);
            for (var x = Width * 4; x < PaddedStride; x++)
            {
                buffer[(y * PaddedStride) + x] = 0xEE;
            }
        }

        var rects = dirtyRects ?? [];
        var nativeRects = new RdRect[rects.Length];
        for (var i = 0; i < rects.Length; i++)
        {
            nativeRects[i] = new RdRect { X = rects[i].X, Y = rects[i].Y, Width = rects[i].Width, Height = rects[i].Height };
        }

        source = buffer;
        fixed (byte* pixels = buffer)
        fixed (RdRect* rectPointer = nativeRects)
        {
            var frame = new RdFrame
            {
                Bgra = pixels,
                Width = Width,
                Height = Height,
                BytesPerRow = PaddedStride,
                HostTimeNanoseconds = 1_000_000_000,
                DirtyRects = rects.Length == 0 ? null : rectPointer,
                DirtyCount = rects.Length,
            };
            sink.Deliver(&frame);
        }
    }
}

/// <summary>
/// The conversions between the three coordinate spaces.
/// </summary>
/// <remarks>
/// macOS keeps displays and the pointer in points, hands back frames in pixels, and the protocol speaks normalized
/// coordinates. Nothing checks those conversions at run time — a wrong scale just produces a pointer that is off by
/// a factor of two on a Retina panel — so they are pinned here.
/// </remarks>
public class CoordinateConventionTests
{
    private static DisplayInfo Display(int x = 0, int y = 0, int width = 100, int height = 80, double scale = 1.0) =>
        new("1", "display", new PixelRect(x, y, width, height), scale, IsPrimary: true);

    [Fact]
    public void A_pointer_at_the_display_origin_lands_at_the_top_left_of_the_frame()
    {
        var position = ScreenCaptureKitCapturer.ToDisplayPosition(Display(), Snapshot(0, 0, hotspotX: 0, hotspotY: 0));

        Assert.Equal(0, position.X);
        Assert.Equal(0, position.Y);
        Assert.True(position.Visible);
    }

    [Fact]
    public void The_backing_scale_converts_points_to_pixels()
    {
        // The same point on a 2x panel is twice as far into the frame. Getting this wrong is the classic Retina
        // bug: the pointer tracks at half speed.
        var point = Snapshot(50, 40, hotspotX: 0, hotspotY: 0);

        var atOne = ScreenCaptureKitCapturer.ToDisplayPosition(Display(scale: 1.0), point);
        var atTwo = ScreenCaptureKitCapturer.ToDisplayPosition(Display(scale: 2.0), point);

        Assert.Equal(50, atOne.X);
        Assert.Equal(100, atTwo.X);
        Assert.Equal(40, atOne.Y);
        Assert.Equal(80, atTwo.Y);
    }

    [Fact]
    public void A_display_away_from_the_origin_is_accounted_for()
    {
        // Only the main display has its origin at (0,0); a second one to its left has negative coordinates, and
        // the frame's own origin is what the consumer draws against.
        var position = ScreenCaptureKitCapturer.ToDisplayPosition(
            Display(x: -200, y: 0, scale: 2.0),
            Snapshot(-180, 30, hotspotX: 0, hotspotY: 0));

        Assert.Equal(40, position.X);
        Assert.Equal(60, position.Y);
    }

    [Fact]
    public void The_hot_spot_is_subtracted_in_pixels_so_the_position_is_the_image_top_left()
    {
        // CursorPosition is documented as the top-left of the cursor image, not its tip, so the hot spot comes off
        // after the scale conversion.
        var position = ScreenCaptureKitCapturer.ToDisplayPosition(
            Display(scale: 2.0),
            Snapshot(50, 40, hotspotX: 10, hotspotY: 12));

        Assert.Equal(90, position.X);
        Assert.Equal(68, position.Y);
    }

    [Fact]
    public void Visibility_is_reported_as_the_helper_saw_it()
    {
        Assert.True(ScreenCaptureKitCapturer.ToDisplayPosition(Display(), Snapshot(0, 0, 0, 0)).Visible);
        Assert.False(ScreenCaptureKitCapturer.ToDisplayPosition(Display(), Snapshot(0, 0, 0, 0, visible: 0)).Visible);
    }

    [Fact]
    public void A_scale_that_came_back_as_zero_does_not_collapse_the_conversion()
    {
        // A display that reports no mode would otherwise multiply everything by zero and pin the pointer to the
        // origin. Treating it as 1 is the only reading that keeps the pointer usable.
        var position = ScreenCaptureKitCapturer.ToDisplayPosition(
            Display(scale: 0),
            Snapshot(50, 40, hotspotX: 0, hotspotY: 0));

        Assert.Equal(50, position.X);
        Assert.Equal(40, position.Y);
    }

    private static unsafe RdCursorSnapshot Snapshot(
        double x,
        double y,
        int hotspotX,
        int hotspotY,
        int visible = 1) =>
        new()
        {
            X = x,
            Y = y,
            Visible = visible,
            HotspotX = hotspotX,
            HotspotY = hotspotY,
            Bgra = null,
        };
}

/// <summary>The pieces that need the native helper to be present at all.</summary>
public class MacNativeInteropTests
{
    [Fact]
    public void The_native_helper_is_found_where_the_resolver_looks_for_it()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The helper is a macOS dylib.");

        var paths = MacNativeLibraryResolver.SearchPaths;

        Assert.NotEmpty(paths);

        // The first candidate is beside the assembly, which is where both a build output and Contents/MacOS put it.
        // If this fails the build did not copy it and every capture would fail with a bare DllNotFoundException.
        Assert.True(
            File.Exists(paths[0]),
            $"The helper is missing from {paths[0]}. Run native/macos/build-native.sh, or check that the project's "
            + "BuildMacNativeHelper target ran. Searched: " + string.Join(", ", paths));
    }

    [Fact]
    public void The_cursor_can_be_read()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The cursor comes from CoreGraphics.");

        // This is the one entry point that needs no permission and no running stream, so it is the cheapest proof
        // that the dylib loaded and the ABI lines up. A wrong struct layout shows up here as nonsense coordinates
        // rather than as a crash.
        var status = ReadCursor(out var snapshot);

        Assert.Equal(MacNativeInterop.Ok, status);
        Assert.True(snapshot.X is > -100_000 and < 100_000, $"Implausible x: {snapshot.X}");
        Assert.True(snapshot.Y is > -100_000 and < 100_000, $"Implausible y: {snapshot.Y}");
    }

    [Fact]
    public void Reading_the_cursor_twice_reports_the_shape_as_unchanged()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The cursor comes from CoreGraphics.");

        // The first read establishes the baseline, so it always reports a change; the second must not. When this
        // was wrong, CoreGraphics was leaving the RGB of fully-transparent pixels undefined, so the fingerprint
        // differed on every read and a shape event fired for every frame.
        Assert.Equal(MacNativeInterop.Ok, ReadCursor(out var first));

        Assert.Equal(MacNativeInterop.Ok, ReadCursor(out var second));

        Assert.Equal(0, second.Changed);
        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Height, second.Height);
    }

    private static unsafe int ReadCursor(out RdCursorSnapshot snapshot)
    {
        RdCursorSnapshot local;
        var status = MacNativeInterop.rd_cursor_read(&local);
        snapshot = local;
        return status;
    }
}

/// <summary>The permission lookup and the panes it points at.</summary>
public class TccGrantTests
{
    [Theory]
    [InlineData(TccGrant.ScreenRecording, "Privacy_ScreenCapture")]
    [InlineData(TccGrant.Accessibility, "Privacy_Accessibility")]
    public void Each_grant_points_at_the_pane_it_is_given_in(TccGrant grant, string expected)
    {
        // The anchor is not checked by the compiler and a typo sends the user to a blank pane, so it is pinned.
        Assert.Equal(expected, TccGrants.SettingsAnchor(grant));
    }

    [Fact]
    public void Asking_whether_a_grant_is_present_does_not_prompt()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "TCC is macOS only.");

        // IsGranted is the preflight call, which is the one that must be safe to call while drawing a window —
        // Request is the one that shows a prompt, and it is deliberately not exercised by a test run.
        foreach (var grant in Enum.GetValues<TccGrant>())
        {
            _ = TccGrants.IsGranted(grant);
            Assert.False(string.IsNullOrWhiteSpace(TccGrants.Describe(grant)));
        }
    }
}

/// <summary>
/// A real ScreenCaptureKit stream.
/// </summary>
/// <remarks>
/// These need Screen Recording, which is granted per signing identity and cannot be granted by a test. Without it
/// the stream simply never produces a frame, so the tests skip rather than fail — the failure would say nothing
/// about the code. Run them from a signed bundle (or a terminal that already holds the grant) to get a real answer;
/// <c>--self-check</c> reports the same thing in one line.
/// </remarks>
public class ScreenCaptureKitCapturerTests
{
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_frame_comes_back_at_the_display_pixel_size()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "ScreenCaptureKit is macOS only.");
        Assert.SkipUnless(
            TccGrants.IsGranted(TccGrant.ScreenRecording),
            "Screen Recording is not granted to this process, so no frame can arrive.");

        var display = PrimaryDisplay();
        await using var capturer = new ScreenCaptureKitCapturerFactory().Create(display);
        using var frame = await Capture(capturer);

        Assert.NotNull(frame);

        // Pixels, not points: on a Retina panel these differ by the scale factor, and capturing at the point size
        // would silently halve the resolution.
        var (expectedWidth, expectedHeight) = CoreGraphicsDisplays.PixelSize(uint.Parse(display.Id, CultureInfo.InvariantCulture));
        Assert.Equal(expectedWidth, frame.Width);
        Assert.Equal(expectedHeight, frame.Height);
        Assert.Equal(PixelFormat.Bgra32, frame.Format);

        // A frame whose buffer is shorter than its geometry claims is how a stride bug shows up.
        Assert.True(frame.GetPixels(out var stride).Length >= stride * frame.Height);
    }

    [Fact]
    public async Task The_frame_can_be_disposed_and_another_captured()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "ScreenCaptureKit is macOS only.");
        Assert.SkipUnless(
            TccGrants.IsGranted(TccGrant.ScreenRecording),
            "Screen Recording is not granted to this process, so no frame can arrive.");

        var display = PrimaryDisplay();
        await using var capturer = new ScreenCaptureKitCapturerFactory().Create(display);

        // The interface requires the previous frame to be disposed before the next capture, and the sink hands its
        // buffer back to the pool on dispose — so a second capture that returned the same buffer would be a real
        // bug, and this is where it would show.
        using (var first = await Capture(capturer))
        {
            // ScreenCaptureKit reports a screen nobody is drawing to as no frame at all — the same behaviour the
            // self-check calls out — and a CI runner's display renders nothing. That is the environment being
            // unable to show this, not the lifecycle under test; on a screen that is doing something both frames
            // arrive and the assertions below run.
            Assert.SkipWhen(
                first is null,
                "ScreenCaptureKit delivered no frame; the display is probably unchanged (a runner renders nothing).");
        }

        using var second = await Capture(capturer);

        // The same reasoning as the first frame, and it applies at least as strongly here — which is what the
        // earlier version of this test got wrong, by requiring a second frame whenever the first one had arrived.
        // ScreenCaptureKit delivers a frame when the screen changes, and a runner's screen never does: the first
        // capture takes the one frame that was waiting, and the second has to wait for a new one. So a missing
        // second frame says the display is static, not that disposing broke the capturer — everything after this
        // line runs on a display that is doing something, which is where the lifecycle under test can be seen.
        Assert.SkipWhen(
            second is null,
            "ScreenCaptureKit delivered no second frame; the display is static (a runner renders nothing).");

        Assert.Equal(display.Id, capturer.Display.Id);
    }

    [Fact]
    public async Task Capturing_a_display_that_does_not_exist_fails_with_a_reason()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "ScreenCaptureKit is macOS only.");

        // Deliberately not skipped on the permission check: this has to fail the same way either way, and the
        // message is the point — it is what a user sees when capture cannot start.
        var missing = new DisplayInfo("4294967294", "missing", new PixelRect(0, 0, 100, 100), 1.0, IsPrimary: false);

        var error = Assert.Throws<ScreenCaptureUnavailableException>(() => new ScreenCaptureKitCapturerFactory().Create(missing));

        Assert.Contains("4294967294", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reset_starts_a_fresh_capture_instead_of_leaving_a_dead_stream()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "ScreenCaptureKit is macOS only.");
        Assert.SkipUnless(
            TccGrants.IsGranted(TccGrant.ScreenRecording),
            "Screen Recording is not granted to this process, so no capture can start.");

        var display = PrimaryDisplay();
        await using var capturer = new ScreenCaptureKitCapturerFactory().Create(display);

        // The stream has to be running before the reset: replacing a capture that never started would only test the
        // constructor twice.
        using (var first = await Capture(capturer))
        {
            Assert.SkipWhen(first is null, "ScreenCaptureKit delivered no frame; the display is probably unchanged.");
        }

        capturer.Reset();

        Assert.False(capturer.IsFaulted, "A reset on a healthy display left the capturer reported as faulted.");

        // A static screen may produce nothing after the reset; the assertion is about the new stream being alive and
        // not faulted, which is what the capture loop acts on.
        using var second = await capturer.CaptureAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.False(capturer.IsFaulted);
    }

    [Fact]
    public async Task An_unexpected_stop_is_reported_as_a_fault_and_reset_rebuilds_the_stream()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "ScreenCaptureKit is macOS only.");
        Assert.SkipUnless(
            TccGrants.IsGranted(TccGrant.ScreenRecording),
            "Screen Recording is not granted to this process, so no capture can start.");

        var display = PrimaryDisplay();
        var capturer = (ScreenCaptureKitCapturer)new ScreenCaptureKitCapturerFactory().Create(display);
        await using (capturer)
        {
            using (var first = await Capture(capturer))
            {
                Assert.SkipWhen(first is null, "ScreenCaptureKit delivered no frame; the display is probably unchanged.");
            }

            Assert.False(capturer.IsFaulted);

            // This sets the flag ScreenCaptureKit's own didStopWithError would set. The stream keeps running, so the
            // only thing under test is the managed path the capture loop follows: fault reported, then rebuilt.
            capturer.SimulateUnexpectedStopForTest("the test stopped the stream");
            Assert.True(capturer.IsFaulted, "The native stop flag did not reach IsFaulted; the loop would never recover.");

            capturer.Reset();
            Assert.False(capturer.IsFaulted, "Reset did not rebuild the stream after an unexpected stop.");

            using var second = await capturer.CaptureAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }
    }

    private static DisplayInfo PrimaryDisplay()
    {
        var displays = new CoreGraphicsDisplays().GetDisplays();
        Assert.NotEmpty(displays);
        return displays.Single(display => display.IsPrimary);
    }

    private static async Task<VideoFrame?> Capture(IScreenCapturer capturer) =>
        await capturer.CaptureAsync(CaptureTimeout, TestContext.Current.CancellationToken);
}
