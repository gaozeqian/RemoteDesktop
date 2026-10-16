using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Client.ViewModels;

namespace RemoteDesktop.Platform.Windows.UnitTests;

public class RecordingFileTests
{
    [Fact]
    public void NextPath_names_the_file_after_the_moment_it_started()
    {
        var dir = NewDirectory();
        try
        {
            var path = RecordingFile.NextPath(dir, "Screen");

            Assert.Equal(dir, Path.GetDirectoryName(path));
            Assert.Matches(@"^Screen_\d{8}_\d{6}\.mp4$", Path.GetFileName(path));
        }
        finally
        {
            Delete(dir);
        }
    }

    [Fact]
    public void NextPath_steps_over_a_name_that_is_already_taken()
    {
        var dir = NewDirectory();
        try
        {
            // The name is only as precise as the second, and two recordings can start inside one (two windows
            // recording the screen, or a stop and a start). Reusing the name would hand a fresh encoder a path whose
            // file the user was just told had been saved, and the writer recreates it.
            var first = RecordingFile.NextPath(dir, "Screen");
            File.WriteAllBytes(first, [1]);

            var second = RecordingFile.NextPath(dir, "Screen");
            Assert.NotEqual(first, second);

            File.WriteAllBytes(second, [1]);
            var third = RecordingFile.NextPath(dir, "Screen");
            Assert.NotEqual(first, third);
            Assert.NotEqual(second, third);
        }
        finally
        {
            Delete(dir);
        }
    }

    [Theory]
    [InlineData(VideoRecorderFailure.PictureSizeChanged)]
    [InlineData(VideoRecorderFailure.WriteFailed)]
    [InlineData(VideoRecorderFailure.NothingRecorded)]
    public void Interrupted_explains_every_failure_the_recorder_can_report(VideoRecorderFailure failure) =>
        Assert.NotNull(RecordingMessages.Interrupted(failure));

    [Fact]
    public void Interrupted_says_nothing_while_the_recording_is_healthy() =>
        Assert.Null(RecordingMessages.Interrupted(VideoRecorderFailure.None));

    [Fact]
    public void StopFailed_keeps_the_reason_so_the_failure_can_be_placed()
    {
        // A recording with no picture is not "a file that may be incomplete": there is no file.
        var empty = RecordingMessages.StopFailed(VideoRecorderFailure.NothingRecorded, new InvalidOperationException("nope"));
        Assert.Contains("没有生成文件", empty, StringComparison.Ordinal);

        // Anything else carries what the encoder or the disk said.
        var other = RecordingMessages.StopFailed(VideoRecorderFailure.None, new InvalidOperationException("disk is full"));
        Assert.Contains("disk is full", other, StringComparison.Ordinal);
    }

    private static string NewDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rec_path_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Delete(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
