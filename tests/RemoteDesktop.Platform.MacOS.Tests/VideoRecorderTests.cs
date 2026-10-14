using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Platform.MacOS.Interop;
using RemoteDesktop.Platform.MacOS.Recording;

namespace RemoteDesktop.Platform.MacOS.Tests;

/// <summary>
/// Exercises the AVAssetWriter-backed recorder the same way the Windows tests exercise Media Foundation: frames in,
/// an MP4 out, and the failure modes the contract promises.
/// </summary>
public class VideoRecorderTests
{
    [Fact]
    public async Task MacVideoRecorder_records_frames_into_mp4_file()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AVAssetWriter is macOS only.");

        var tempPath = Path.Combine(Path.GetTempPath(), $"rec_test_{Guid.NewGuid():N}.mp4");
        var recorder = new MacVideoRecorder();
        try
        {
            const int width = 320;
            const int height = 240;
            const int fps = 30;

            recorder.Start(tempPath, width, height, fps, bitrateKbps: 1000);
            Assert.True(recorder.IsRecording);
            Assert.Equal(tempPath, recorder.OutputFilePath);

            var frame = new byte[width * height * 4];
            for (var i = 0; i < frame.Length; i++)
            {
                frame[i] = (byte)((i * 37) & 0xFF);
            }

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

            // An MP4 always begins with an 'ftyp' box; a finalized file that produced no playable bytes would not.
            Assert.Equal("ftyp", ReadFourCc(savedPath));
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
    public async Task MacVideoRecorder_writes_out_the_frames_still_queued_when_the_stop_begins()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AVAssetWriter is macOS only.");

        var tempPath = Path.Combine(Path.GetTempPath(), $"rec_test_{Guid.NewGuid():N}.mp4");
        var recorder = new MacVideoRecorder();
        try
        {
            const int width = 64;
            const int height = 64;
            recorder.Start(tempPath, width, height, frameRate: 30, bitrateKbps: 500);

            // Handed over the moment they are queued, so every frame is still in the channel when the drain starts —
            // and far fewer than the queue holds, so none are dropped as back-pressure.
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
    public async Task MacVideoRecorder_says_so_when_no_frame_ever_arrived()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AVAssetWriter is macOS only.");

        var tempPath = Path.Combine(Path.GetTempPath(), $"rec_test_{Guid.NewGuid():N}.mp4");
        var recorder = new MacVideoRecorder();
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
    public async Task MacVideoRecorder_reports_a_picture_whose_size_changed()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AVAssetWriter is macOS only.");

        var tempPath = Path.Combine(Path.GetTempPath(), $"rec_test_{Guid.NewGuid():N}.mp4");
        var recorder = new MacVideoRecorder();
        try
        {
            recorder.Start(tempPath, 64, 64, frameRate: 30, bitrateKbps: 500);
            var frame = new byte[32 * 32 * 4];

            // The writer's input type is fixed at Start, so this frame can never go in: the recording has to say so
            // rather than keep a clock running over a file that stopped growing.
            recorder.RecordFrame(frame, 32 * 4, 32, 32);

            Assert.Equal(VideoRecorderFailure.PictureSizeChanged, recorder.Failure);
            Assert.Equal(0, recorder.WrittenFrames);

            // Nothing reached the writer, so there is no recording: AVAssetWriter will not finalize such a file, and
            // what it leaves behind is not playable — the recorder says so and takes the file away.
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

    [Fact]
    public async Task MacVideoRecorder_writes_the_picture_the_right_way_up()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AVAssetWriter is macOS only.");

        const int width = 320;
        const int height = 240;
        var tempPath = Path.Combine(Path.GetTempPath(), $"rec_test_{Guid.NewGuid():N}.mp4");
        var recorder = new MacVideoRecorder();
        try
        {
            recorder.Start(tempPath, width, height, frameRate: 30, bitrateKbps: 2000);

            // Red above blue, rows top-down exactly as the viewers hand them over. CVPixelBuffer memory is also
            // top-row-first, so a recorder that passes the rows straight through writes this picture the right way up —
            // unlike Media Foundation, which is bottom-up and would need a flip.
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

            var (top, bottom) = ProbeOrientation(tempPath);
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

    /// <summary>Reads the file's first frame back through AVAssetReader and returns the luma of its top and bottom rows.</summary>
    private static (double Top, double Bottom) ProbeOrientation(string path)
    {
        double top = 0;
        double bottom = 0;
        var error = new byte[MacNativeInterop.ErrorBufferSize];
        unsafe
        {
            fixed (byte* errorPointer = error)
            {
                var status = MacNativeInterop.rd_recorder_probe_orientation(path, out top, out bottom, errorPointer, (nuint)error.Length);
                Assert.Equal(MacNativeInterop.Ok, status);
            }
        }

        return (top, bottom);
    }

    private static string ReadFourCc(string path)
    {
        var bytes = new byte[8];
        using (var stream = File.OpenRead(path))
        {
            stream.ReadExactly(bytes);
        }

        // The 'ftyp' box: 4-byte size, then the 4-byte type.
        return System.Text.Encoding.ASCII.GetString(bytes, 4, 4);
    }
}
