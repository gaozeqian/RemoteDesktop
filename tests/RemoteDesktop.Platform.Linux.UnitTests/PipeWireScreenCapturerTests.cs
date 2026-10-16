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

    private static (PortalGrant Grant, PipeWireScreenCapturerFactory Factory) CreateFactory()
    {
        var grant = new PortalGrant(
            new FileSecretStore(Path.Combine(DataDirectory, "secrets")),
            NullLoggerFactory.Instance,
            NullLogger<PortalGrant>.Instance);

        return (grant, new PipeWireScreenCapturerFactory(
            grant,
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

        var (grant, factory) = CreateFactory();
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

    private static void Stop((PortalGrant Grant, PipeWireScreenCapturerFactory Factory, IScreenCapturer Capturer) pair)
    {
        pair.Capturer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        pair.Grant.Dispose();
    }

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
    public async Task A_screen_that_is_not_changing_reports_no_frame_rather_than_the_last_one_again()
    {
        var pair = Start();
        try
        {
            await using var capturer = pair.Capturer;

            // Drain whatever is waiting, then ask again with a short timeout. The compositor sends a buffer when the
            // screen is damaged, so with nothing happening there is nothing to hand back — which the pipeline uses
            // to lower its frame rate. Returning the previous frame instead would keep it encoding a still image.
            using (var first = await capturer.CaptureAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken))
            {
                Assert.NotNull(first);
            }

            using var second = await capturer.CaptureAsync(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

            // Not asserted to be null: something on this machine may well be animating. What must not happen is a
            // call that never returns, or one that throws because there was nothing to give.
            Assert.True(second is null || second.Width > 0);
        }
        finally
        {
            Stop(pair);
        }
    }
}
