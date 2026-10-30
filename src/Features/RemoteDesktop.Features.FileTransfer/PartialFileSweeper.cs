using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RemoteDesktop.Features.FileTransfer;

/// <summary>
/// Deletes the unfinished transfers nobody came back for.
/// </summary>
/// <remarks>
/// Sweeps when the host starts and then every <see cref="Interval"/>. Starting a session is still not held up by it —
/// the first pass runs on a worker thread — but a machine that stays up for weeks no longer keeps abandoned parts
/// until its next restart, which is what a list that only counted on startup amounted to. A file that is being
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

    /// <summary>
    /// How often the sweep runs. A transfer that expires is removed within one interval of the retention time, and a
    /// record whose file is gone is dropped within one as well.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                // Off the startup path: starting a session should not wait for a directory walk.
                await Task.Run(Sweep, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One pass that fails is no reason to stop the next one; the list itself is untouched.
                LogSweepFailed(logger, ex.Message);
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed the unfinished transfer {Path}: nobody came back for it.")]
    private static partial void LogRemoved(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Kept the unfinished transfer {Path}: {Reason}")]
    private static partial void LogKept(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A sweep of the unfinished transfers failed: {Reason}")]
    private static partial void LogSweepFailed(ILogger logger, string reason);
}
