using System.Diagnostics;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Windows.Camera;

namespace RemoteDesktop.Platform.Windows.UnitTests;

/// <summary>The Windows camera path with a real camera; skipped on machines without one. Pictures are only measured, never stored.</summary>
public class CameraTests
{
    /// <summary>Opt-in gate for the rate case: what it measures belongs to the camera and its wiring, not to this code.</summary>
    private const string RateGate = "RD_CAMERA_RATE_TEST";

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
            // Encoders reject odd dimensions, and the narrowest mode is still one a viewer would use.
            Assert.All(camera.Modes, m =>
            {
                Assert.Equal(0, m.Width % 2);
                Assert.Equal(0, m.Height % 2);
                Assert.True(m.Width >= 160, $"mode {m} is narrower than an encoder accepts.");
                Assert.True(m.FrameRate >= 5, $"mode {m} is slower than a usable stream.");
            });
            Assert.Equal(camera.Modes.Distinct().Count(), camera.Modes.Count);
        }
    }

    /// <summary>The pictures themselves: format, size, stride and live content. Every machine with a camera owes us this.</summary>
    [Fact]
    public async Task A_real_camera_delivers_nv12_pictures_of_the_requested_size()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only.");
        var camera = Enumerator.GetCameras().FirstOrDefault(c => c.Modes.Count > 0);
        Assert.SkipWhen(camera is null, "No camera is connected.");

        var run = await CaptureFortyAsync(camera);

        Assert.Equal(40, run.Pictures);
        Assert.True(run.Differed, "Successive pictures never differed (sensor noise alone should change them).");
        TestContext.Current.SendDiagnosticMessage($"{camera.Name} {run.Mode}: {run.FramesPerSecond:0.0} fps measured");
    }

    /// <summary>
    /// The rate the pictures actually arrive at. Opt-in, because it measures the device and its wiring rather than
    /// this code: a 4K camera on a USB 2 port, or one in low light, delivers a fraction of the mode it advertises.
    /// </summary>
    [Fact]
    public async Task A_real_camera_delivers_pictures_at_about_the_requested_rate()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only.");
        var camera = Enumerator.GetCameras().FirstOrDefault(c => c.Modes.Count > 0);
        Assert.SkipWhen(camera is null, "No camera is connected.");
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(RateGate) == "1",
            $"The rate depends on the camera and how it is wired (a 4K device on USB 2 delivers a fraction of its "
            + $"mode); set {RateGate}=1 on hardware that sustains the mode it advertises.");

        var run = await CaptureFortyAsync(camera);

        Assert.InRange(run.FramesPerSecond, run.Mode.FrameRate * 0.4, run.Mode.FrameRate * 1.3);
    }

    /// <summary>What one forty-picture run of the camera measured.</summary>
    private sealed record CameraRun(CameraMode Mode, int Pictures, bool Differed, double FramesPerSecond);

    /// <summary>
    /// Runs the camera for forty pictures and checks every one of them — NV12 at the requested size, a stride that
    /// matches the width, the NV12 byte count, and luma that varies once exposure has settled. The rate is returned
    /// rather than asserted: only the case that opted in to it judges that.
    /// </summary>
    private static async Task<CameraRun> CaptureFortyAsync(CameraInfo camera)
    {
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

                // Live data, not an empty or constant buffer: once exposure settles past the first few frames,
                // the luma varies across the picture and the camera has exposure.
                var luma = pixels[..(frame.Width * frame.Height)];
                var sample = new byte[256];
                for (var i = 0; i < sample.Length; i++)
                {
                    sample[i] = luma[i * (luma.Length / sample.Length)];
                }

                if (count > 2)
                {
                    Assert.True(sample.Distinct().Count() > 3, "The picture looks constant.");
                }
                differing |= previous is not null && !previous.AsSpan().SequenceEqual(sample);
                previous = sample;
            }

            if (++count == 40)
            {
                break;
            }
        }

        clock.Stop();
        return new CameraRun(mode, count, differing, 39 / clock.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Cameras that offer only YUY2 at their smaller sizes cannot be converted by the source reader and used to
    /// fail with MF_E_INVALIDMEDIATYPE. The source now asks for YUY2 and reduces it here; the frame is NV12 either way.
    /// </summary>
    [Fact]
    public void A_yuy2_buffer_is_reduced_to_nv12()
    {
        // 4x2: each row is two Y0 U Y1 V pairs.
        var yuy2 = new byte[]
        {
            10, 100, 20, 200, 30, 110, 40, 210,
            50, 120, 60, 220, 70, 130, 80, 230,
        };

        using var frame = new MemoryVideoFrame(4, 2, PixelFormat.Nv12, TimeSpan.Zero);
        MediaFoundationCameraSource.CopyYuy2ToNv12(yuy2, 4, 2, frame);

        Assert.Equal(
            new byte[] { 10, 20, 30, 40, 50, 60, 70, 80, 110, 210, 120, 220 },
            frame.Buffer.ToArray());
    }

    [Fact]
    public async Task The_smallest_mode_is_still_delivered_as_nv12()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only.");
        var camera = Enumerator.GetCameras().FirstOrDefault(c => c.Modes.Count > 0);
        Assert.SkipWhen(camera is null, "No camera is connected.");
        var mode = camera.Modes[^1];

        await using var source = new MediaFoundationCameraSourceFactory(Enumerator).Open(camera.Id);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(TimeSpan.FromSeconds(20));
        await foreach (var frame in source.CaptureAsync(mode, cancel.Token))
        {
            using (frame)
            {
                Assert.Equal(PixelFormat.Nv12, frame.Format);
                Assert.Equal((mode.Width, mode.Height), (frame.Width, frame.Height));
            }

            break;
        }
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
