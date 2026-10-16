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

                    await recorder.StopAsync();
                }
                catch (Exception ex)
                {
                    failures.Add($"take {i}: {ex.GetType().Name}: {ex.Message}");
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
