using Microsoft.Extensions.Logging;

namespace RemoteDesktop.Features.FileTransfer;

/// <summary>Where the list of unfinished transfers is kept; empty means nothing is tracked or swept.</summary>
/// <param name="Path">The file holding the list.</param>
public sealed record PartialFileLedgerOptions(string Path)
{
    /// <summary>Whether a list is worth keeping at all.</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(Path);
}

/// <summary>
/// Remembers the partial files this machine has been asked to keep so that a transfer can continue later.
/// </summary>
/// <remarks>
/// <para>
/// An interrupted transfer leaves <c>名称.大小-时间.rdpart</c> beside where the file was going, on purpose: that is
/// what makes continuing it possible. But a controller that starts transfers it never finishes leaves one file per
/// attempt, each with a different size in its name, in whatever folder it was pointed at — and nothing else on this
/// machine knows those files are ours or would ever remove them. This is that list; see
/// <see cref="PartialFileSweeper"/> for what acts on it.
/// </para>
/// <para>
/// Writes go through a temporary file and a move, so a list that is interrupted is never half a list. Reading is
/// best effort: a list that cannot be read costs the cleanup, not the transfers.
/// </para>
/// </remarks>
public sealed partial class PartialFileLedger
{
    private readonly Lock _lock = new();
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<PartialFileLedger> _logger;
    private bool _loaded;

    public PartialFileLedger(PartialFileLedgerOptions options, ILogger<PartialFileLedger> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        LedgerPath = options.Path;
        _logger = logger;
    }

    /// <summary>The file the list is written to.</summary>
    public string LedgerPath { get; }

    /// <summary>Notes a partial file. Called before the first byte arrives, so a process that is killed midway has
    /// already left the record behind.</summary>
    public void Record(string partialPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partialPath);

        lock (_lock)
        {
            LoadIfNeeded();
            if (_paths.Add(partialPath))
            {
                Save();
            }
        }
    }

    /// <summary>Drops a partial file from the list: it was finished, discarded or deleted.</summary>
    public void Forget(string partialPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partialPath);

        lock (_lock)
        {
            LoadIfNeeded();
            if (_paths.Remove(partialPath))
            {
                Save();
            }
        }
    }

    /// <summary>The files that were unfinished the last time the list was written.</summary>
    public IReadOnlyList<string> Entries()
    {
        lock (_lock)
        {
            LoadIfNeeded();
            return [.. _paths];
        }
    }

    private void LoadIfNeeded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            if (!File.Exists(LedgerPath))
            {
                return;
            }

            foreach (var line in File.ReadAllLines(LedgerPath))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _paths.Add(line.Trim());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogUnreadable(_logger, LedgerPath, ex.Message);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LedgerPath)!);
            var temporary = LedgerPath + ".tmp";
            File.WriteAllLines(temporary, _paths);

            // Written beside the file and moved into place: a list that is interrupted must not become half a list.
            File.Move(temporary, LedgerPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The transfer itself is unaffected; the cost is that this file is not cleaned up later.
            LogUnwritable(_logger, LedgerPath, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The list of unfinished transfers at {Path} could not be read: {Reason}")]
    private static partial void LogUnreadable(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The list of unfinished transfers at {Path} could not be written: {Reason}")]
    private static partial void LogUnwritable(ILogger logger, string path, string reason);
}
