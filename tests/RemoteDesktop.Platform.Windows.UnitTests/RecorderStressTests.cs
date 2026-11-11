using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Windows.Recording;

namespace RemoteDesktop.Platform.Windows.UnitTests;

public class RecorderStressTests
{
    [Fact]
    public async Task MfVideoRecorder_stops_reliably_over_many_recordings()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only.");

        var directory = Path.Combine(Path.GetTempPath(), $"rec_stress_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var failures = new List<string>();
        try
        {
            for (var i = 0; i < 40; i++)
            {
                var path = Path.Combine(directory, $"take_{i}.mp4");
                var recorder = new MfVideoRecorder();
                try
                {
                    recorder.Start(path, 128, 96, 30, 500);
                    var frame = new byte[128 * 96 * 4];
                    for (var f = 0; f < 3; f++)
                    {
                        recorder.RecordFrame(frame, 128 * 4, 128, 96);
                    }

                    var saved = await recorder.StopAsync();

                    // Not throwing is not the same as having recorded: a take that dropped every frame or wrote an
                    // empty file would still come back without an exception, which is the failure this loop is for.
                    Assert.Equal(VideoRecorderFailure.None, recorder.Failure);
                    Assert.True(recorder.WrittenFrames > 0, $"take {i}: no frame reached the encoder");
                    Assert.True(new FileInfo(saved).Length > 0, $"take {i}: {saved} is empty");
                }
                catch (Exception ex)
                {
                    // The stack is what makes a failure in take 17 of 40 diagnosable; the type and message alone
                    // do not say where in the take it came from.
                    failures.Add($"take {i}: {ex}");
                }
                finally
                {
                    await recorder.DisposeAsync();
                }
            }

            Assert.Empty(failures);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }
}
