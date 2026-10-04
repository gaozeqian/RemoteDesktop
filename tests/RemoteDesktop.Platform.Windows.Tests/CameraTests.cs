using System.Diagnostics;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Windows.Camera;

namespace RemoteDesktop.Platform.Windows.Tests;

/// <summary>The Windows camera path with a real camera; skipped on machines without one. Pictures are only measured, never stored.</summary>
public class CameraTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly MediaFoundationCameraEnumerator Enumerator = new();

    [Fact]
    public void Cameras_are_listed_with_names_and_sensible_modes()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only.");
        var cameras = Enumerator.GetCameras();
        Assert.SkipWhen(cameras.Count == 0, "No camera is connected.");

        foreach (var camera in cameras)
        {
            TestContext.Current.SendDiagnosticMessage($"{camera.Name}: {camera.Modes.Count} modes, best {(camera.Modes.Count > 0 ? camera.Modes[0] : null)}");
            Assert.False(string.IsNullOrWhiteSpace(camera.Id));
            Assert.False(string.IsNullOrWhiteSpace(camera.Name));
            Assert.All(camera.Modes, m =>
            {
                Assert.True(m.Width % 2 == 0 && m.Height % 2 == 0 && m.Width >= 160 && m.FrameRate >= 5);
            });
            Assert.Equal(camera.Modes.Distinct().Count(), camera.Modes.Count);
        }
    }

    [Fact]
    public async Task A_real_camera_delivers_nv12_pictures_at_about_the_requested_rate()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only.");
        var camera = Enumerator.GetCameras().FirstOrDefault(c => c.Modes.Count > 0);
        Assert.SkipWhen(camera is null, "No camera is connected.");
        var mode = camera.Modes.FirstOrDefault(m => m.Width == 1280 && m.Height == 720 && m.FrameRate >= 15) ?? camera.Modes[^1];

        await using var source = new MediaFoundationCameraSourceFactory(Enumerator).Open(camera.Id);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(TimeSpan.FromSeconds(30));

        var count = 0;
        var clock = new Stopwatch();
        var differing = false;
        byte[]? previous = null;
        await foreach (var frame in source.CaptureAsync(mode, limit.Token))
        {
            using (frame)
            {
                if (count == 0)
                {
                    clock.Start(); // the first picture arrives after the camera's start-up
                }

                Assert.Equal(PixelFormat.Nv12, frame.Format);
                Assert.Equal((mode.Width, mode.Height), (frame.Width, frame.Height));
                var pixels = frame.GetPixels(out var stride);
                Assert.Equal(frame.Width, stride);
                Assert.Equal(frame.Width * frame.Height * 3 / 2, pixels.Length);

                // Live data, not an empty or constant buffer: the luma varies across the picture and the camera has exposure.
                var luma = pixels[..(frame.Width * frame.Height)];
                var sample = new byte[256];
                for (var i = 0; i < sample.Length; i++)
                {
                    sample[i] = luma[i * (luma.Length / sample.Length)];
                }

                Assert.True(sample.Distinct().Count() > 3, "The picture looks constant.");
                differing |= previous is not null && !previous.AsSpan().SequenceEqual(sample);
                previous = sample;
            }

            if (++count == 40)
            {
                break;
            }
        }

        clock.Stop();
        var rate = 39 / clock.Elapsed.TotalSeconds;
        TestContext.Current.SendDiagnosticMessage($"{camera.Name} {mode}: {rate:0.0} fps measured");
        Assert.Equal(40, count);
        Assert.True(differing, "Successive pictures never differed (sensor noise alone should change them).");
        Assert.InRange(rate, mode.FrameRate * 0.4, mode.FrameRate * 1.3);
    }

    [Fact]
    public async Task Stopping_the_capture_releases_the_camera_so_it_can_be_opened_again()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only.");
        var camera = Enumerator.GetCameras().FirstOrDefault(c => c.Modes.Count > 0);
        Assert.SkipWhen(camera is null, "No camera is connected.");
        var mode = camera.Modes[^1];

        for (var round = 0; round < 2; round++)
        {
            await using var source = new MediaFoundationCameraSourceFactory(Enumerator).Open(camera.Id);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            cancel.CancelAfter(TimeSpan.FromSeconds(30));
            await foreach (var frame in source.CaptureAsync(mode, cancel.Token))
            {
                frame.Dispose();
                break; // leaving the loop disposes the enumerator, which must stop the reader
            }
        }
    }

    [Fact]
    public void An_unknown_camera_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only.");
        Assert.Throws<CameraUnavailableException>(() => new MediaFoundationCameraSourceFactory(Enumerator).Open("not-a-camera"));
    }
}
