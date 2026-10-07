using System.Diagnostics;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Windows.Recording;

namespace RemoteDesktop.Platform.Windows.Tests;

/// <summary>
/// The recorder at the size and rate a real desktop throws at it, which the small-picture tests never exercise:
/// a 1440p BGRA frame is ~14.7 MB, and the queue holds up to thirty of them.
/// </summary>
public class RealisticRecordingTests
{
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public async Task MfVideoRecorder_stops_cleanly_after_a_stream_of_desktop_sized_frames(int width, int height)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only.");

        var tempPath = Path.Combine(Path.GetTempPath(), $"rec_big_{width}x{height}_{Guid.NewGuid():N}.mp4");
        var recorder = new MfVideoRecorder();
        try
        {
            recorder.Start(tempPath, width, height, frameRate: 30, bitrateKbps: 4000);
            var frame = new byte[width * height * 4];
            var clock = Stopwatch.StartNew();

            // Sixty frames at the recording's own rate: two seconds of a real desktop.
            for (var i = 0; i < 60; i++)
            {
                new Random(i).NextBytes(frame);
                recorder.RecordFrame(frame, width * 4, width, height);
                var due = TimeSpan.FromMilliseconds((i + 1) * 1000 / 30);
                var wait = due - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, TestContext.Current.CancellationToken);
                }
            }

            var stopwatch = Stopwatch.StartNew();
            var saved = await recorder.StopAsync();
            stopwatch.Stop();

            Assert.Equal(tempPath, saved);
            Assert.Equal(VideoRecorderFailure.None, recorder.Failure);
            Assert.True(recorder.WrittenFrames > 0, "no frame reached the encoder");
            WriteProgress($"      {width}x{height}: {recorder.WrittenFrames} frames, stop took {stopwatch.ElapsedMilliseconds} ms");
            Assert.True(new FileInfo(saved).Length > 0);
        }
        finally
        {
            await recorder.DisposeAsync();
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private static void WriteProgress(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);
}
