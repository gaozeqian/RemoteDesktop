using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RemoteDesktop.Features.FileTransfer;

/// <summary>
/// Deletes the unfinished transfers nobody came back for.
/// </summary>
/// <remarks>
/// Runs once, when the host starts: a session that begins when somebody logs in is soon enough for files that are
/// counted in days, and it means no timer is left running on a machine that does not need one. A file that is being
/// written right now has a current modification time and is left alone, whatever the list says.
/// </remarks>
public sealed partial class PartialFileSweeper(
    PartialFileLedger ledger,
    TimeProvider time,
    ILogger<PartialFileSweeper> logger) : BackgroundService
{
    /// <summary>
    /// How long an unfinished transfer is kept. Long enough to survive a weekend between two attempts at the same
    /// file; not so long that a folder quietly collects them.
    /// </summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <returns>How many files were removed.</returns>
    public int Sweep()
    {
        var removed = 0;
        foreach (var entry in ledger.Entries())
        {
            try
            {
                var file = new FileInfo(entry);
                if (!file.Exists)
                {
                    // Finished, renamed, or deleted by hand: either way there is nothing left to keep track of.
                    ledger.Forget(entry);
                    continue;
                }

                if (time.GetUtcNow() - file.LastWriteTimeUtc < Retention)
                {
                    continue;
                }

                file.Delete();
                ledger.Forget(entry);
                removed++;
                LogRemoved(logger, entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Locked or unreachable: it stays on the list and gets another chance the next time this runs.
                LogKept(logger, entry, ex.Message);
            }
        }

        return removed;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Off the startup path: starting a session should not wait for a directory walk.
        return Task.Run(Sweep, stoppingToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed the unfinished transfer {Path}: nobody came back for it.")]
    private static partial void LogRemoved(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Kept the unfinished transfer {Path}: {Reason}")]
    private static partial void LogKept(ILogger logger, string path, string reason);
}
