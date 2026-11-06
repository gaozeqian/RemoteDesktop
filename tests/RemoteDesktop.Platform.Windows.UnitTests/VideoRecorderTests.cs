using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Windows.Camera;
using RemoteDesktop.Platform.Windows.MediaFoundation;
using RemoteDesktop.Platform.Windows.Recording;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace RemoteDesktop.Platform.Windows.UnitTests;

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
    public async Task MfVideoRecorder_writes_the_picture_the_right_way_up()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only.");

        const int width = 320;
        const int height = 240;
        var tempPath = Path.Combine(Path.GetTempPath(), $"rec_test_{Guid.NewGuid():N}.mp4");
        var recorder = new MfVideoRecorder();
        try
        {
            recorder.Start(tempPath, width, height, frameRate: 30, bitrateKbps: 2000);

            // Red above blue, rows top-down exactly as the viewers hand them over. Media Foundation reads RGB32 in
            // system memory bottom-up (the DIB convention), so a recorder that passes the rows straight through
            // writes this picture upside down — which is invisible in the solid and noise frames the other tests use.
            var frame = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                var red = y < height / 2;
                for (var x = 0; x < width; x++)
                {
                    var p = ((y * width) + x) * 4;
                    frame[p] = red ? (byte)0 : (byte)255;      // B
                    frame[p + 2] = red ? (byte)255 : (byte)0;  // R
                    frame[p + 3] = 255;                        // A
                }
            }

            for (var i = 0; i < 5; i++)
            {
                recorder.RecordFrame(frame, width * 4, width, height);
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            await recorder.StopAsync();

            var (top, bottom) = LumaOfTheFirstAndLastRows(tempPath, width, height);
            // Red is ~76 in luma and blue ~29, so a flipped picture puts the two the other way round.
            Assert.InRange(top, 60, 95);
            Assert.InRange(bottom, 15, 45);
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

    /// <summary>Reads the file's first picture back through Media Foundation and returns the luma of its top and bottom rows.</summary>
    private static (double Top, double Bottom) LumaOfTheFirstAndLastRows(string path, int width, int height)
    {
        using var attributes = MediaFactory.MFCreateAttributes(1);
        // Lets the reader insert the decoder and the colour converter: the file holds H.264, not NV12.
        attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u).CheckError();
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);
        using (var output = MfRuntime.CreateVideoType(MfGuids.VideoFormatNv12, width, height, 0))
        {
            reader.SetCurrentMediaType(MediaFoundationCameraSource.FirstVideoStream, output);
        }

        using var sample = reader.ReadSample(MediaFoundationCameraSource.FirstVideoStream, 0, out _, out var flags, out _);
        Assert.False(flags.HasFlag(SourceReaderFlag.Error), "the recording could not be read back");
        Assert.NotNull(sample);

        // NV12 is top-down, and both its planes share one stride: the Y plane is height rows of it, the chroma
        // another height / 2. Reading the top row first is what makes this a check on the picture's way up.
        var pixels = MfRuntime.ToArray(sample);
        var stride = pixels.Length / (height * 3 / 2);
        Assert.True(stride >= width, $"the Y plane stride {stride} is narrower than the picture");
        return (Average(pixels.AsSpan(0, width)), Average(pixels.AsSpan((height - 1) * stride, width)));
    }

    private static double Average(ReadOnlySpan<byte> row)
    {
        var sum = 0;
        foreach (var value in row)
        {
            sum += value;
        }

        return (double)sum / row.Length;
    }

    [Fact]
    public async Task MfVideoRecorder_stop_without_a_running_recording_returns_the_latest_path_instead_of_throwing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only.");

        var recorder = new MfVideoRecorder();
        try
        {
            // Never started: no path yet.
            Assert.Equal(string.Empty, await recorder.StopAsync());

            var tempPath = Path.Combine(Path.GetTempPath(), $"rec_test_{Guid.NewGuid():N}.mp4");
            try
            {
                recorder.Start(tempPath, 64, 64, frameRate: 30, bitrateKbps: 500);
                recorder.RecordFrame(new byte[64 * 64 * 4], 64 * 4, 64, 64);
                Assert.Equal(tempPath, await recorder.StopAsync());

                // Already stopped: the same path again, no exception.
                Assert.Equal(tempPath, await recorder.StopAsync());
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
        }
        finally
        {
            await recorder.DisposeAsync();
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
