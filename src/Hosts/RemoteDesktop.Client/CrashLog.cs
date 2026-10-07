namespace RemoteDesktop.Client;

/// <summary>
/// Writes down what would otherwise vanish. The client is a windowed app with no log file, so a crash takes its
/// reason with it and the user is left with a window that disappeared.
/// </summary>
/// <remarks>
/// Instrumentation only: nothing here decides what the process does with an exception. Handling UI-thread
/// exceptions and carrying on is a product decision, and it is deliberately not made here.
/// </remarks>
internal static class CrashLog
{
    private const long MaxBytes = 1024 * 1024;

    public static void Register(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var path = Path.Combine(dataDirectory, "crash.log");

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(path, "fatal", e.ExceptionObject as Exception);

        // A task nobody awaited: recording it is the whole point, and observing it keeps it from being reported as
        // unobserved a second time.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(path, "task", e.Exception);
            e.SetObserved();
        };
    }

    internal static void Write(string path, string source, Exception? exception)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.Exists && file.Length > MaxBytes)
            {
                // Keep the most recent history rather than growing without bound.
                File.Move(path, path + ".1", overwrite: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {source}: {exception}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // If the crash log itself fails there is nowhere left to say so.
        }
    }
}
