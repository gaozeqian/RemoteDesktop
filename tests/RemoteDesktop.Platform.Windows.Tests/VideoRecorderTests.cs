using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Windows.Recording;

namespace RemoteDesktop.Platform.Windows.Tests;

public class VideoRecorderTests
{
    [Fact]
    public async Task MfVideoRecorder_records_frames_into_mp4_file()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only.");

        var tempPath = Path.Combine(Path.GetTempPath(), $"rec_test_{Guid.NewGuid():N}.mp4");
        var recorder = new MfVideoRecorder();
        try
        {
            const int width = 320;
            const int height = 240;
            const int fps = 30;

            recorder.Start(tempPath, width, height, fps, bitrateKbps: 1000);
            Assert.True(recorder.IsRecording);
            Assert.Equal(tempPath, recorder.OutputFilePath);

            var frame = new byte[width * height * 4];
            Array.Fill(frame, (byte)200);

            for (var i = 0; i < 15; i++)
            {
                recorder.RecordFrame(frame, width * 4, width, height);
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            var savedPath = await recorder.StopAsync();
            Assert.False(recorder.IsRecording);
            Assert.Equal(tempPath, savedPath);
            Assert.Equal(VideoRecorderFailure.None, recorder.Failure);
            Assert.Equal(15, recorder.WrittenFrames);

            var fi = new FileInfo(savedPath);
            Assert.True(fi.Exists);
            Assert.True(fi.Length > 0);
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

    [Fact]
    public async Task MfVideoRecorder_writes_out_the_frames_still_queued_when_the_stop_begins()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only.");

        var tempPath = Path.Combine(Path.GetTempPath(), $"rec_test_{Guid.NewGuid():N}.mp4");
        var recorder = new MfVideoRecorder();
        try
        {
            const int width = 64;
            const int height = 64;
            recorder.Start(tempPath, width, height, frameRate: 30, bitrateKbps: 500);

            // Stopped the moment they are handed over, so every frame is still in the queue when the drain starts —
            // and far fewer than the queue holds, so none can be dropped as back-pressure instead.
            const int frames = 5;
            var frame = new byte[width * height * 4];
            for (var i = 0; i < frames; i++)
            {
                recorder.RecordFrame(frame, width * 4, width, height);
            }

            await recorder.StopAsync();

            Assert.Equal(frames, recorder.WrittenFrames);
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

    [Fact]
    public async Task MfVideoRecorder_says_so_when_no_frame_ever_arrived()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only.");

        var tempPath = Path.Combine(Path.GetTempPath(), $"rec_test_{Guid.NewGuid():N}.mp4");
        var recorder = new MfVideoRecorder();
        try
        {
            recorder.Start(tempPath, 64, 64, frameRate: 30, bitrateKbps: 500);

            // A screen that never changes produces no frames at all, so this is what a recording of one looks like.
            await Assert.ThrowsAsync<InvalidOperationException>(recorder.StopAsync);

            Assert.Equal(VideoRecorderFailure.NothingRecorded, recorder.Failure);
            Assert.False(File.Exists(tempPath), "an empty recording must not be left behind");
        }
        finally
        {
            await recorder.DisposeAsync();
        }
    }

    [Fact]
    public async Task MfVideoRecorder_reports_a_picture_whose_size_changed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only.");

        var tempPath = Path.Combine(Path.GetTempPath(), $"rec_test_{Guid.NewGuid():N}.mp4");
        var recorder = new MfVideoRecorder();
        try
        {
            recorder.Start(tempPath, 64, 64, frameRate: 30, bitrateKbps: 500);
            var frame = new byte[32 * 32 * 4];

            // The encoder's input type is fixed at Start, so this frame can never go in: the recording has to say so
            // rather than keep a clock running over a file that stopped growing.
            recorder.RecordFrame(frame, 32 * 4, 32, 32);

            Assert.Equal(VideoRecorderFailure.PictureSizeChanged, recorder.Failure);
            Assert.Equal(0, recorder.WrittenFrames);

            // Nothing reached the encoder, so there is no recording: Media Foundation will not finalize such a file,
            // and what it leaves behind is not playable — the recorder says so and takes the file away.
            await Assert.ThrowsAsync<InvalidOperationException>(recorder.StopAsync);
            Assert.False(File.Exists(tempPath));
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
}
