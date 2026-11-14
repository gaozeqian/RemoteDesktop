using System.ComponentModel;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Platform.Windows.Display;

namespace RemoteDesktop.Platform.Windows.UnitTests;

/// <summary>
/// The GDI fallback capturer's fault flag. The failure path used to be reachable only on a session whose desktop had
/// gone away, so the flag that exists to tell a static screen from a dead capture was never exercised.
/// </summary>
public sealed class GdiScreenCapturerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_failed_blit_sets_IsFaulted_and_the_next_success_clears_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI capture is Windows only.");

        var display = new DisplayInfo(
            "primary",
            "Primary",
            new PixelRect(0, 0, 64, 64),
            1,
            IsPrimary: true);

        try
        {
            await using var capturer = new GdiScreenCapturer(display);

            capturer.BitBltOverride = () => false;
            var failed = await capturer.CaptureAsync(TimeSpan.FromMilliseconds(1), Ct);
            Assert.Null(failed);
            Assert.True(capturer.IsFaulted, "A failed copy has to be visible to the capture loop.");

            // The next successful copy is what clears it: a static screen must not stay marked broken.
            capturer.BitBltOverride = () => true;
            var frame = await capturer.CaptureAsync(TimeSpan.Zero, Ct);
            Assert.NotNull(frame);
            Assert.False(capturer.IsFaulted);
            frame.Dispose();
        }
        catch (Win32Exception)
        {
            // No interactive desktop (a service session, or a runner without one): there is nothing to blit from.
            Assert.Skip("No interactive desktop is available for GDI capture.");
        }
    }
}
