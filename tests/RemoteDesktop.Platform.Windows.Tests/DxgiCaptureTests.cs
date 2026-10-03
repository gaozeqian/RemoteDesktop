using System.Diagnostics;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Media;
using RemoteDesktop.Platform.Windows.Display;
using SharpGen.Runtime;

namespace RemoteDesktop.Platform.Windows.Tests;

/// <summary>Needs an interactive desktop; skipped where duplication is unavailable (services, CI without a session).</summary>
public class DxgiCaptureTests
{
    [Fact]
    public void Enumerates_a_primary_display()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DXGI is Windows only.");
        var displays = new DxgiDisplayEnumerator().GetDisplays();
        Assert.SkipWhen(displays.Count == 0, "No display attached.");

        Assert.Single(displays, d => d.IsPrimary);
        Assert.All(displays, d => Assert.True(d.Bounds.Width > 0 && d.Bounds.Height > 0));
    }

    [Fact]
    public async Task Captures_the_primary_display_and_converts_it_to_nv12()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DXGI is Windows only.");
        var primary = new DxgiDisplayEnumerator().GetDisplays().FirstOrDefault(d => d.IsPrimary);
        Assert.SkipWhen(primary is null, "No display attached.");

        DxgiScreenCapturer capturer;
        try
        {
            capturer = new DxgiScreenCapturer(primary!);
        }
        catch (SharpGenException ex)
        {
            Assert.Skip($"Desktop duplication unavailable: {ex.Message}");
            return;
        }

        await using (capturer)
        {
            var stopwatch = Stopwatch.StartNew();
            using var frame = await capturer.CaptureAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            var captureMs = stopwatch.Elapsed.TotalMilliseconds;

            Assert.NotNull(frame);
            Assert.Equal(PixelFormat.Bgra32, frame.Format);
            Assert.Equal(primary!.Bounds.Width, frame.Width);
            Assert.Equal(primary.Bounds.Height, frame.Height);

            var width = frame.Width & ~1;
            var height = frame.Height & ~1;
            using var nv12 = new MemoryVideoFrame(width, height, PixelFormat.Nv12, frame.Timestamp);
            stopwatch.Restart();
            ColorConversion.BgraToNv12(frame.GetPixels(out var stride), stride, width, height, nv12.Buffer, nv12.Stride);
            TestContext.Current.SendDiagnosticMessage(
                $"Captured {frame.Width}x{frame.Height} in {captureMs:0.0} ms, BGRA→NV12 {stopwatch.Elapsed.TotalMilliseconds:0.0} ms");
        }
    }
}
