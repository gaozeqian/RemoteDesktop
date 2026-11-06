using Microsoft.Extensions.Logging;

namespace RemoteDesktop.Features.FileTransfer;

/// <summary>Where the list of unfinished transfers is kept; empty means nothing is tracked or swept.</summary>
/// <param name="Path">The file holding the list.</param>
/// <param name="MaxEntries">
/// How many partial files are tracked at once. A controller that abandons one attempt after another adds a record each
/// time, so the list needs an upper bound; the newest records are the ones a resumed transfer can still use.
/// </param>
public sealed record PartialFileLedgerOptions(string Path, int MaxEntries = PartialFileLedger.DefaultMaxEntries)
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
/// The list is bounded: past <see cref="PartialFileLedgerOptions.MaxEntries"/> the oldest record is dropped, so a host
/// cannot be made to keep — or rewrite — an ever longer file. Writing is a whole-file rewrite through a temporary file
/// and a move, which keeps an interrupted write from leaving half a list behind and keeps the per-write cost at the
/// cap rather than at the number of attempts ever made. Reading is best effort: a list that cannot be read costs the
/// cleanup, not the transfers.
/// </para>
/// </remarks>
public sealed partial class PartialFileLedger
{
    /// <summary>The number of records kept when the caller does not say otherwise.</summary>
    public const int DefaultMaxEntries = 1024;

    private readonly Lock _lock = new();

    // Serialises the disk write and rejects stale snapshots. The snapshot is taken under _lock, but the write itself
    // runs under _writeLock so a slow disk never blocks the in-memory bookkeeping of another transfer.
    private readonly Lock _writeLock = new();
    private readonly Dictionary<string, LinkedListNode<string>> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = new();
    private readonly ILogger<PartialFileLedger> _logger;
    private readonly int _maxEntries;
    private bool _loaded;
    private long _saveVersion;
    private long _writtenVersion;

    public PartialFileLedger(PartialFileLedgerOptions options, ILogger<PartialFileLedger> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        LedgerPath = options.Path;
        _maxEntries = Math.Max(1, options.MaxEntries);
        _logger = logger;
    }

    /// <summary>The file the list is written to.</summary>
    public string LedgerPath { get; }

    /// <summary>Notes a partial file. Called before the first byte arrives, so a process that is killed midway has
    /// already left the record behind.</summary>
    public void Record(string partialPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partialPath);

        if (partialPath.AsSpan().ContainsAny('\r', '\n'))
        {
            // The list holds one path per line; a path with a line break in it would be read back as several
            // unrelated entries. Not tracking it only costs the cleanup of this one file.
            LogNotTracked(_logger);
            return;
        }

        string[] snapshot;
        long version;
        lock (_lock)
        {
            LoadIfNeeded();
            if (_paths.ContainsKey(partialPath))
            {
                return;
            }

            _paths.Add(partialPath, _order.AddLast(partialPath));
            while (_order.Count > _maxEntries)
            {
                DropOldest();
            }

            version = ++_saveVersion;
            snapshot = [.. _order];
        }

        Save(snapshot, version);
    }

    /// <summary>Drops a partial file from the list: it was finished, discarded or deleted.</summary>
    public void Forget(string partialPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partialPath);

        string[]? snapshot = null;
        long version = 0;
        lock (_lock)
        {
            LoadIfNeeded();
            if (_paths.Remove(partialPath, out var node))
            {
                _order.Remove(node);
                version = ++_saveVersion;
                snapshot = [.. _order];
            }
        }

        if (snapshot is not null)
        {
            Save(snapshot, version);
        }
    }

    /// <summary>The files that were unfinished the last time the list was written.</summary>
    public IReadOnlyList<string> Entries()
    {
        lock (_lock)
        {
            LoadIfNeeded();
            return [.. _order];
        }
    }

    private void DropOldest()
    {
        var oldest = _order.First;
        if (oldest is null)
        {
            return;
        }

        _order.RemoveFirst();
        _paths.Remove(oldest.Value);
        LogEvicted(_logger, oldest.Value);
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
                var path = line.Trim();
                if (path.Length > 0 && !_paths.ContainsKey(path))
                {
                    _paths.Add(path, _order.AddLast(path));
                }
            }

            // A list written before the cap existed, or by hand, may be longer than this process will keep.
            while (_order.Count > _maxEntries)
            {
                DropOldest();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogUnreadable(_logger, LedgerPath, ex.Message);
        }
    }

    private void Save(string[] entries, long version)
    {
        lock (_writeLock)
        {
            if (version <= _writtenVersion)
            {
                // A newer snapshot has already been written; this stale one must not overwrite it.
                return;
            }

            _writtenVersion = version;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LedgerPath)!);
                var temporary = LedgerPath + ".tmp";
                File.WriteAllLines(temporary, entries);

                // Written beside the file and moved into place: a list that is interrupted must not become half a list.
                File.Move(temporary, LedgerPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The transfer itself is unaffected; the cost is that this file is not cleaned up later.
                LogUnwritable(_logger, LedgerPath, ex.Message);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "An unfinished transfer is not tracked for cleanup: its path contains a line break, which the list cannot store.")]
    private static partial void LogNotTracked(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The list of unfinished transfers at {Path} could not be read: {Reason}")]
    private static partial void LogUnreadable(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The list of unfinished transfers at {Path} could not be written: {Reason}")]
    private static partial void LogUnwritable(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopped tracking the unfinished transfer {Path}: the list is full and it is the oldest record.")]
    private static partial void LogEvicted(ILogger logger, string path);
}
