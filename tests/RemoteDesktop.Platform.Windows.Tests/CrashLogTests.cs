using RemoteDesktop.Client;

namespace RemoteDesktop.Platform.Windows.Tests;

public class CrashLogTests
{
    [Fact]
    public void Write_records_where_the_exception_came_from()
    {
        var dir = NewDirectory();
        try
        {
            var path = Path.Combine(dir, "crash.log");
            CrashLog.Write(path, "task", new InvalidOperationException("boom"));

            var text = File.ReadAllText(path);
            Assert.Contains("task", text, StringComparison.Ordinal);
            Assert.Contains("boom", text, StringComparison.Ordinal);
        }
        finally
        {
            Delete(dir);
        }
    }

    [Fact]
    public void Write_rolls_the_file_over_instead_of_growing_without_bound()
    {
        var dir = NewDirectory();
        try
        {
            var path = Path.Combine(dir, "crash.log");
            File.WriteAllBytes(path, new byte[(1024 * 1024) + 1]);

            CrashLog.Write(path, "fatal", new InvalidOperationException("second"));

            Assert.True(File.Exists(path + ".1"));
            Assert.Contains("second", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            Delete(dir);
        }
    }

    private static string NewDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"crash_log_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Delete(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
