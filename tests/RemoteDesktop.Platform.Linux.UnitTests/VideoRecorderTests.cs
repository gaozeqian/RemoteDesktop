using System.Buffers.Binary;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Linux.Video;

namespace RemoteDesktop.Platform.Linux.UnitTests;

/// <summary>
/// Recording frames into an MP4 file.
/// </summary>
/// <remarks>
/// The file is checked by reading its own structure rather than by playing it: an MP4 that was never finished has no
/// <c>moov</c> box, and that is the difference between a recording a person can open and a file that was written but
/// cannot be played. Nothing here needs a media player to notice it.
/// </remarks>
public sealed class VideoRecorderTests : IDisposable
{
    private const string LinuxOnly = "libavcodec and libavformat, through the Linux platform layer.";

    private const int Width = 320;
    private const int Height = 240;
    private const int Stride = Width * 4;

    private readonly string _folder = Directory.CreateTempSubdirectory("rd-record-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string PathFor(string name) => Path.Combine(_folder, name + ".mp4");

    /// <summary>A BGRA frame of one colour, which is what a still screen looks like.</summary>
    private static byte[] Frame(byte value)
    {
        var pixels = new byte[Stride * Height];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = value;
            pixels[i + 1] = (byte)(value / 2);
            pixels[i + 2] = (byte)(255 - value);
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    [Fact]
    public async Task Recording_frames_produces_a_file_that_can_be_played()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var path = PathFor("session");
        await using var recorder = new FfmpegVideoRecorder();

        recorder.Start(path, Width, Height, frameRate: 30, bitrateKbps: 1500);
        Assert.True(recorder.IsRecording);

        var frame = Frame(0x40);
        for (var i = 0; i < 30; i++)
        {
            recorder.RecordFrame(frame, Stride, Width, Height);
        }

        var written = await recorder.StopAsync();

        Assert.Equal(path, written);
        Assert.False(recorder.IsRecording);
        Assert.Equal(VideoRecorderFailure.None, recorder.Failure);
        Assert.Equal(path, recorder.OutputFilePath);
        Assert.True(recorder.WrittenFrames > 0, "No frame reached the encoder.");

        Assert.True(File.Exists(path), "The recording is not there.");

        var boxes = TopLevelBoxes(path);
        Assert.Equal("ftyp", boxes[0].Type);

        // moov is written when the recording is finished. Without it the file holds picture data that nothing can
        // find its way into, and a recorder that never wrote the trailer would look like it had worked.
        Assert.Contains(boxes, box => box.Type == "moov");
        Assert.Contains(boxes, box => box.Type == "mdat" && box.Size > 0);
    }

    [Fact]
    public async Task A_recording_of_nothing_says_so_and_leaves_no_file_behind()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var path = PathFor("empty");
        await using var recorder = new FfmpegVideoRecorder();
        recorder.Start(path, Width, Height);

        // No frame ever reached the encoder, so there is nothing to finalize — and a file holding no picture is
        // worse than no file, because it looks like a recording until somebody tries to open it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StopAsync());

        Assert.Equal(VideoRecorderFailure.NothingRecorded, recorder.Failure);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_picture_that_changes_size_stops_the_recording_and_says_why()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var path = PathFor("resized");
        await using var recorder = new FfmpegVideoRecorder();
        recorder.Start(path, Width, Height);

        var frame = Frame(0x40);
        recorder.RecordFrame(frame, Stride, Width, Height);

        // The file's frames are one size; a picture that changes size cannot go into it. Reported rather than
        // silently dropped, so the caller knows where the recording ends.
        recorder.RecordFrame(frame, Stride, Width, Height);

        var smaller = new byte[160 * 4 * 120];
        recorder.RecordFrame(smaller, 160 * 4, 160, 120);

        Assert.Equal(VideoRecorderFailure.PictureSizeChanged, recorder.Failure);

        // What was recorded before stays valid and is still finalized.
        await recorder.StopAsync();
        Assert.Contains(TopLevelBoxes(path), box => box.Type == "moov");
    }

    [Fact]
    public async Task The_first_reason_is_the_one_that_is_kept()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var path = PathFor("reasons");
        await using var recorder = new FfmpegVideoRecorder();
        recorder.Start(path, Width, Height);

        var frame = Frame(0x40);
        recorder.RecordFrame(frame, Stride, Width, Height);
        recorder.RecordFrame(frame, Stride, Width, Height);

        recorder.RecordFrame(new byte[16], 8, 2, 2); // the wrong shape
        Assert.Equal(VideoRecorderFailure.PictureSizeChanged, recorder.Failure);

        // A later frame that fails for another reason does not displace the first: the first is what explains where
        // the file ends.
        recorder.RecordFrame(frame, Stride, Width, Height);
        Assert.Equal(VideoRecorderFailure.PictureSizeChanged, recorder.Failure);

        await recorder.StopAsync();
    }

    [Fact]
    public async Task A_padded_frame_without_the_padding_after_its_last_row_is_accepted()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var path = PathFor("padded");
        await using var recorder = new FfmpegVideoRecorder();
        recorder.Start(path, Width, Height);

        // Rows 64 bytes wider than the picture, and a buffer that ends where the last visible pixel does: the
        // contract is the visible bytes of every row, the same as the Windows and macOS recorders accept.
        const int paddedStride = Stride + 64;
        var pixels = new byte[((Height - 1) * paddedStride) + Stride];
        recorder.RecordFrame(pixels, paddedStride, Width, Height);

        await recorder.StopAsync();

        Assert.Equal(VideoRecorderFailure.None, recorder.Failure);
        Assert.True(recorder.WrittenFrames > 0, "The frame was refused.");
    }

    [Fact]
    public async Task More_frames_than_the_queue_holds_are_dropped_rather_than_queued()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var path = PathFor("flood");
        await using var recorder = new FfmpegVideoRecorder();
        recorder.Start(path, Width, Height, frameRate: 30);

        // Far more than the queue holds, handed over as fast as possible. The recording may be short of frames; what
        // must not happen is the queue growing until the machine runs out of memory.
        var frame = Frame(0x40);
        for (var i = 0; i < 600; i++)
        {
            recorder.RecordFrame(frame, Stride, Width, Height);
        }

        await recorder.StopAsync();

        Assert.True(File.Exists(path), "The recording is not there.");
        Assert.Equal(VideoRecorderFailure.None, recorder.Failure);
        Assert.True(recorder.WrittenFrames > 0, "Nothing was recorded at all.");
    }

    [Fact]
    public async Task The_recorder_says_how_long_it_has_been_going()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        await using var recorder = new FfmpegVideoRecorder();
        Assert.Equal(TimeSpan.Zero, recorder.Duration);

        recorder.Start(PathFor("duration"), Width, Height);
        await Task.Delay(150, TestContext.Current.CancellationToken);

        Assert.True(recorder.Duration >= TimeSpan.FromMilliseconds(100), $"The recorder reported {recorder.Duration}.");
        Assert.Equal(VideoRecorderFailure.None, recorder.Failure);

        recorder.RecordFrame(Frame(0x40), Stride, Width, Height);
        await recorder.StopAsync();
    }

    [Fact]
    public async Task Reporting_a_recording_the_wrong_shape_is_refused_before_it_starts()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        await using var recorder = new FfmpegVideoRecorder();

        Assert.Throws<ArgumentOutOfRangeException>(() => recorder.Start(PathFor("bad"), 1, 1));
        Assert.Throws<ArgumentException>(() => recorder.Start(string.Empty, Width, Height));
    }

    /// <summary>
    /// The top-level boxes of an MP4 file, in the order they appear.
    /// </summary>
    /// <remarks>
    /// A box is a big-endian length followed by a four-character type; a length of one means the real length is a
    /// 64-bit value after the type. Enough to tell a finished file from an abandoned one, which is the question.
    /// </remarks>
    private static List<(string Type, long Size)> TopLevelBoxes(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var boxes = new List<(string, long)>();
        var offset = 0;

        while (offset + 8 <= bytes.Length)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4);

            if (size == 1)
            {
                size = (uint)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset + 8));
                boxes.Add((type, size));
                offset += (int)size;
                continue;
            }

            if (size < 8)
            {
                break;
            }

            boxes.Add((type, size));
            offset += (int)size;
        }

        return boxes;
    }
}
