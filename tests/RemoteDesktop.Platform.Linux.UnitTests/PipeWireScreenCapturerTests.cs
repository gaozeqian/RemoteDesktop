using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Linux.Display;
using RemoteDesktop.Platform.Linux.XdgPortal;
using RemoteDesktop.Platform.Posix;

namespace RemoteDesktop.Platform.Linux.UnitTests;

/// <summary>
/// Capturing a screen through the portal's PipeWire connection.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in, like the portal tests and for the same reason: this asks the desktop for a screen, and on a machine that
/// has never granted one that means a dialog with a Share button in it.
/// </para>
/// <para>
/// The tests share the application's own secret store rather than one of their own. The restore token is what says
/// the desktop has already been asked, and a test that kept its own copy would ask a second time for something the
/// product was already granted — and would be testing a grant the product does not actually have.
/// </para>
/// <para>
/// What these cover that nothing else does: that bytes come out the other end at all, that they are the screen
/// rather than a plausible-looking pattern of the wrong shape, and that a still screen reports no change instead of
/// handing out the same frame forever.
/// </para>
/// </remarks>
public sealed class PipeWireScreenCapturerTests
{
    private const string LinuxOnly = "The desktop portals and PipeWire, which only exist on Linux.";
    private const string NeedsConsent = "Needs a person to click Share; set RD_PORTAL_TEST=1 to run it.";

    private static bool Enabled => Environment.GetEnvironmentVariable("RD_PORTAL_TEST") == "1";

    private static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteDesktop");

    private static (PortalGrant Grant, PipeWireScreenCapturerFactory Factory) CreateFactory(IDisplayEnumerator displays)
    {
        var grant = new PortalGrant(
            new FileSecretStore(Path.Combine(DataDirectory, "secrets")),
            NullLoggerFactory.Instance,
            NullLogger<PortalGrant>.Instance);

        return (grant, new PipeWireScreenCapturerFactory(
            grant,
            displays,
            NullLoggerFactory.Instance,
            NullLogger<PipeWireScreenCapturerFactory>.Instance));
    }

    private static WaylandDisplayEnumerator CreateEnumerator() =>
        new(NullLogger<WaylandDisplayEnumerator>.Instance);

    /// <summary>Sets up a capturer for the primary screen, or skips the test with a reason.</summary>
    private static (PortalGrant Grant, PipeWireScreenCapturerFactory Factory, IScreenCapturer Capturer) Start()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
        Assert.SkipWhen(!Enabled, NeedsConsent);
        Assert.SkipUnless(MutterDisplayConfig.IsAvailable(), "No display layout to capture.");

        using var displays = CreateEnumerator();
        var target = displays.GetDisplays().FirstOrDefault(d => d.IsPrimary)
            ?? throw new InvalidOperationException("No primary screen to capture.");

        var (grant, factory) = CreateFactory(displays);
        try
        {
            return (grant, factory, factory.Create(target));
        }
        catch
        {
            grant.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Releases the portal grant. The capturer is not touched here: every case holds it in an <c>await using</c>, so
    /// disposing it again would be a second release of the same object.
    /// </summary>
    private static void Stop((PortalGrant Grant, PipeWireScreenCapturerFactory Factory, IScreenCapturer Capturer) pair) =>
        pair.Grant.Dispose();

    [Fact]
    public async Task A_frame_arrives_the_size_of_the_screen()
    {
        var pair = Start();
        try
        {
            await using var capturer = pair.Capturer;

            using var frame = await capturer.CaptureAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

            Assert.NotNull(frame);

            // The compositor delivers at the monitor's mode, which is the physical resolution — not the scaled
            // rectangle the portal describes the screen with. DisplayInfo.Bounds is the physical one, and the
            // encoder downstream expects a frame of that size.
            Assert.Equal(capturer.Display.Bounds.Width, frame.Width);
            Assert.Equal(capturer.Display.Bounds.Height, frame.Height);

            var pixels = frame.GetPixels(out var stride);
            Assert.Equal(PixelFormat.Bgra32, frame.Format);
            Assert.Equal(frame.Width * 4, stride);
            Assert.Equal(stride * frame.Height, pixels.Length);
        }
        finally
        {
            Stop(pair);
        }
    }

    [Fact]
    public async Task The_picture_is_the_screen_rather_than_a_pattern_of_the_right_shape()
    {
        var pair = Start();
        try
        {
            await using var capturer = pair.Capturer;

            using var frame = await capturer.CaptureAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            Assert.NotNull(frame);

            var pixels = frame.GetPixels(out var stride);
            var colours = new HashSet<uint>();

            // Sampled coarsely: a desktop has thousands of colours, and the failures worth catching here — a stride
            // that does not match the producer's, pixels read from the wrong offset, a buffer of one repeated value
            // — all show up as a handful.
            for (var y = 0; y < frame.Height; y += 4)
            {
                for (var x = 0; x < frame.Width; x += 4)
                {
                    var at = (y * stride) + (x * 4);
                    colours.Add((uint)(pixels[at] | (pixels[at + 1] << 8) | (pixels[at + 2] << 16)));
                }
            }

            Assert.True(colours.Count > 50, $"The frame holds only {colours.Count} distinct colours, so it is not a picture of anything.");
        }
        finally
        {
            Stop(pair);
        }
    }

    [Fact]
    public async Task A_second_capture_has_a_bounded_wait_even_when_the_desktop_is_animating()
    {
        var pair = Start();
        try
        {
            await using var capturer = pair.Capturer;

            // This opt-in desktop test proves only the bounded wait: the machine may be animating. Deterministic
            // timeout/null behavior is covered by the controlled-channel test below.
            using (var first = await capturer.CaptureAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken))
            {
                Assert.NotNull(first);
            }

            var waited = Stopwatch.StartNew();
            using var second = await capturer.CaptureAsync(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
            waited.Stop();

            // Not asserted to be null: something on this machine may well be animating. What must not happen is a
            // call that never returns, or one that throws because there was nothing to give.
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(3), $"a 250 ms wait took {waited.Elapsed}, so it was not bounded.");
            Assert.True(second is null || second.Width > 0, "a picture came back with no width.");
        }
        finally
        {
            Stop(pair);
        }
    }

    [Fact]
    public async Task Caller_cancellation_is_not_a_static_screen_timeout()
    {
        var capturer = EmptyCapturer();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await capturer.CaptureAsync(TimeSpan.FromSeconds(1), cancelled.Token));
    }

    [Fact]
    public async Task A_wait_timeout_still_means_no_changed_frame()
    {
        var capturer = EmptyCapturer();

        var frame = await capturer.CaptureAsync(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);

        Assert.Null(frame);
    }

    [Fact]
    public async Task A_closed_pipewire_channel_is_a_fault_not_an_endless_empty_frame()
    {
        var capturer = EmptyCapturer();
        capturer.CompleteForTests();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await capturer.CaptureAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.True(capturer.IsFaulted);
    }

    private static PipeWireScreenCapturer EmptyCapturer() =>
        new(new DisplayInfo("test", "Test", new PixelRect(0, 0, 1, 1), 1, true));
}
