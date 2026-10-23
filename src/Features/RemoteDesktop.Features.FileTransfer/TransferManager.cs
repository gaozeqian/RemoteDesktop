using System.Diagnostics;
using RemoteDesktop.Protocol.File;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Features.FileTransfer;

public enum TransferDirection
{
    /// <summary>From this computer to the remote one.</summary>
    Upload,

    /// <summary>From the remote computer to this one.</summary>
    Download,
}

public enum TransferState
{
    Queued,
    Running,

    /// <summary>Stopped by the user; what was received so far is kept and <see cref="TransferManager.Resume"/> continues it.</summary>
    Paused,
    Completed,

    /// <summary>Not copied because the file exists and the conflict policy says to keep it.</summary>
    Skipped,
    Failed,
    Cancelled,
}

/// <summary>One file in the transfer list. Changes are announced through <see cref="Changed"/> on whatever thread made them.</summary>
public sealed class TransferJob : ITransferObserver
{
    private static readonly double StopwatchToSeconds = 1.0 / Stopwatch.Frequency;

    private long _transferred;
    private long _total;
    private int _state;
    private long _speedMark;
    private long _speedBytes;
    private long _bytesPerSecond;
    private long _lastChanged;
    private volatile string? _error;
    private volatile string _finalPath = string.Empty;
    private volatile string _partialPath = string.Empty;

    internal TransferJob(int id, TransferDirection direction, string fileName, string sourcePath, string destinationDirectory, ConflictPolicy policy, long totalBytes)
    {
        Id = id;
        Direction = direction;
        FileName = fileName;
        SourcePath = sourcePath;
        DestinationDirectory = destinationDirectory;
        ConflictPolicy = policy;
        _total = totalBytes;
    }

    public event EventHandler? Changed;

    public int Id { get; }

    public TransferDirection Direction { get; }

    public string FileName { get; }

    public string SourcePath { get; }

    public string DestinationDirectory { get; }

    public ConflictPolicy ConflictPolicy { get; }

    public long TotalBytes => Volatile.Read(ref _total);

    public long TransferredBytes => Volatile.Read(ref _transferred);

    public TransferState State => (TransferState)Volatile.Read(ref _state);

    /// <summary>Why the transfer failed, in words for the user.</summary>
    public string? Error => _error;

    /// <summary>Where the file ends up (known once the transfer has been agreed).</summary>
    public string FinalPath => _finalPath;

    public double BytesPerSecond => BitConverter.Int64BitsToDouble(Volatile.Read(ref _bytesPerSecond));

    public double Fraction => TotalBytes == 0 ? (State == TransferState.Completed ? 1 : 0) : Math.Min(1, (double)TransferredBytes / TotalBytes);

    /// <summary>True while the job still has work to do or is waiting for the user.</summary>
    public bool IsActive => State is TransferState.Queued or TransferState.Running;

    internal string PartialPath => _partialPath;

    internal CancellationTokenSource? RunToken { get; set; }

    internal StopReason Stop { get; set; }

    void ITransferObserver.Started(long totalBytes, long startOffset, string finalPath, string partialPath)
    {
        Volatile.Write(ref _total, totalBytes);
        Volatile.Write(ref _transferred, startOffset);
        _finalPath = finalPath;
        _partialPath = partialPath;
        ResetSpeed(startOffset);
        RaiseChanged(force: true);
    }

    void ITransferObserver.Progress(long bytes)
    {
        Volatile.Write(ref _transferred, bytes);
        UpdateSpeed(bytes);
        RaiseChanged(force: false);
    }

    internal void SetState(TransferState state, string? error = null)
    {
        _error = error;
        Volatile.Write(ref _state, (int)state);
        if (state != TransferState.Running)
        {
            Volatile.Write(ref _bytesPerSecond, BitConverter.DoubleToInt64Bits(0));
        }

        if (state == TransferState.Completed)
        {
            Volatile.Write(ref _transferred, TotalBytes);
        }
        else if (state is TransferState.Queued or TransferState.Running)
        {
            ResetSpeed(TransferredBytes);
        }

        RaiseChanged(force: true);
    }

    private void ResetSpeed(long bytes)
    {
        Volatile.Write(ref _speedMark, Stopwatch.GetTimestamp());
        Volatile.Write(ref _speedBytes, bytes);
    }

    // Measured over at least half a second so the figure does not jump with every chunk; smoothed with the previous value.
    private void UpdateSpeed(long bytes)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - Volatile.Read(ref _speedMark)) * StopwatchToSeconds;
        if (elapsed < 0.5)
        {
            return;
        }

        var rate = (bytes - Volatile.Read(ref _speedBytes)) / elapsed;
        var previous = BytesPerSecond;
        Volatile.Write(ref _bytesPerSecond, BitConverter.DoubleToInt64Bits(previous == 0 ? rate : (previous * 0.6) + (rate * 0.4)));
        Volatile.Write(ref _speedMark, now);
        Volatile.Write(ref _speedBytes, bytes);
    }

    // Chunks arrive many times a second; listeners only need a few updates per second.
    private void RaiseChanged(bool force)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && (now - Volatile.Read(ref _lastChanged)) * StopwatchToSeconds < 0.1)
        {
            return;
        }

        Volatile.Write(ref _lastChanged, now);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal enum StopReason
    {
        None,
        Pause,
        Cancel,
        Shutdown,
    }
}

/// <summary>
/// The transfer list of the file manager: queues files (expanding folders), runs a few at a time, and lets the user
/// pause, resume, cancel and retry. Pausing keeps the partial file, so resuming (or retrying after a failure or a lost
/// connection) continues instead of starting over.
/// </summary>
public sealed class TransferManager : IAsyncDisposable
{
    public const int DefaultConcurrency = 3;

    /// <summary>Guards against link cycles when a folder tree is walked.</summary>
    private const int MaxDepth = 64;

    private readonly IPeerConnection _connection;
    private readonly IFileSource _local;
    private readonly IFileSource _remote;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _lock = new();
    private readonly List<TransferJob> _jobs = [];
    private readonly List<Task> _running = [];
    private int _nextId;

    public TransferManager(IPeerConnection connection, IFileSource local, IFileSource remote, int maxConcurrent = DefaultConcurrency)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrent, 1);
        _connection = connection;
        _local = local;
        _remote = remote;
        _slots = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public event EventHandler<TransferJob>? JobAdded;

    public IReadOnlyList<TransferJob> Jobs
    {
        get
        {
            lock (_lock)
            {
                return [.. _jobs];
            }
        }
    }

    /// <summary>
    /// Queues the chosen items of <paramref name="sourceFolder"/> for copying into <paramref name="destinationFolder"/> on the other
    /// side. Folders are walked and recreated at the destination; each file becomes a job.
    /// </summary>
    public async Task<IReadOnlyList<TransferJob>> EnqueueAsync(
        TransferDirection direction,
        string sourceFolder,
        IReadOnlyList<FileEntryInfo> items,
        string destinationFolder,
        ConflictPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        var (from, to) = direction == TransferDirection.Upload ? (_local, _remote) : (_remote, _local);
        var created = new List<TransferJob>();
        foreach (var item in items)
        {
            await AddItemAsync(direction, from, to, from.Combine(sourceFolder, item.Name), destinationFolder, item, policy, created, depth: 0, cancellationToken).ConfigureAwait(false);
        }

        return created;
    }

#pragma warning disable CA1822 // Part of the manager's instance API next to Resume and Cancel, even though pausing needs no manager state.
    public void Pause(TransferJob job)
#pragma warning restore CA1822
    {
        ArgumentNullException.ThrowIfNull(job);
        StopRunning(job, TransferJob.StopReason.Pause);
    }

    /// <summary>Continues a paused or failed job from what it already has.</summary>
    public void Resume(TransferJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.State is TransferState.Paused or TransferState.Failed)
        {
            Start(job);
        }
    }

    /// <summary>Stops a job for good and removes what was received so far.</summary>
    public void Cancel(TransferJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.State is TransferState.Queued or TransferState.Running)
        {
            StopRunning(job, TransferJob.StopReason.Cancel);
        }
        else if (job.State is TransferState.Paused or TransferState.Failed)
        {
            job.SetState(TransferState.Cancelled);
            _ = DiscardPartialAsync(job);
        }
    }

    /// <summary>Waits until nothing is queued or running. Mostly for tests and shutdown.</summary>
    public async Task WhenIdleAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task[] running;
            lock (_lock)
            {
                _running.RemoveAll(t => t.IsCompleted);
                running = [.. _running];
            }

            if (running.Length == 0)
            {
                return;
            }

            await Task.WhenAny(Task.WhenAll(running), Task.Delay(50, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public async ValueTask DisposeAsync()
    {
        TransferJob[] active;
        lock (_lock)
        {
            active = [.. _jobs.Where(j => j.IsActive)];
        }

        foreach (var job in active)
        {
            job.Stop = TransferJob.StopReason.Shutdown;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        Task[] running;
        lock (_lock)
        {
            running = [.. _running];
        }

        await Task.WhenAll(running).ConfigureAwait(false);
        _shutdown.Dispose();
        _slots.Dispose();
    }

    private async Task AddItemAsync(
        TransferDirection direction,
        IFileSource from,
        IFileSource to,
        string sourcePath,
        string destinationFolder,
        FileEntryInfo item,
        ConflictPolicy policy,
        List<TransferJob> created,
        int depth,
        CancellationToken cancellationToken)
    {
        // The name comes from the side being read, and on a download that side is the remote computer. A listing is
        // just a message: nothing stops it from calling a folder "../../.ssh" or "/home/user/.config/autostart".
        // The file path was already checked when the job was created (FileTransferClient.DownloadAsync calls
        // NormalizeName), but directories are joined here, and a recursive download would create - and then write
        // files into - whatever this produced. Validate it with the destination's own rules before it is used.
        string name;
        try
        {
            name = FileAccessPolicy.NormalizeName(item.Name);
        }
        catch (FileOperationException ex)
        {
            var rejected = CreateJob(direction, item.Name, sourcePath, destinationFolder, policy, item.Size);
            created.Add(rejected);
            rejected.SetState(TransferState.Failed, ex.Message);
            return;
        }

        if (!item.IsDirectory)
        {
            var job = CreateJob(direction, name, sourcePath, destinationFolder, policy, item.Size);
            created.Add(job);
            Start(job);
            return;
        }

        try
        {
            var target = to.Combine(destinationFolder, name);
            if (depth >= MaxDepth)
            {
                throw new FileOperationException(FileError.Io, "The folders are nested too deeply.");
            }

            try
            {
                await to.MakeDirectoryAsync(target, cancellationToken).ConfigureAwait(false);
            }
            catch (FileOperationException ex) when (ex.Error == FileError.AlreadyExists)
            {
                // Merging into an existing folder is what the user expects.
            }

            var contents = await from.ListAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            foreach (var child in contents.Entries)
            {
                await AddItemAsync(direction, from, to, from.Combine(sourcePath, child.Name), target, child, policy, created, depth + 1, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (FileOperationException ex)
        {
            // Shown in the list like any other failed item, so the rest of the tree still goes ahead.
            var job = CreateJob(direction, item.Name, sourcePath, destinationFolder, policy, 0);
            created.Add(job);
            job.SetState(TransferState.Failed, ex.Message);
        }
    }

    private TransferJob CreateJob(TransferDirection direction, string name, string sourcePath, string destinationFolder, ConflictPolicy policy, long size)
    {
        var job = new TransferJob(Interlocked.Increment(ref _nextId), direction, name, sourcePath, destinationFolder, policy, size);
        lock (_lock)
        {
            _jobs.Add(job);
        }

        JobAdded?.Invoke(this, job);
        return job;
    }

    private void Start(TransferJob job)
    {
        var run = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        job.RunToken = run;
        job.Stop = TransferJob.StopReason.None;
        job.SetState(TransferState.Queued);
        var task = Task.Run(() => ExecuteAsync(job, run), CancellationToken.None);
        lock (_lock)
        {
            _running.Add(task);
        }
    }

    private static void StopRunning(TransferJob job, TransferJob.StopReason reason)
    {
        job.Stop = reason;
        try
        {
            job.RunToken?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Just finished on its own.
        }
    }

    private async Task ExecuteAsync(TransferJob job, CancellationTokenSource run)
    {
        var acquired = false;
        try
        {
            await _slots.WaitAsync(run.Token).ConfigureAwait(false);
            acquired = true;
            job.SetState(TransferState.Running);

            var outcome = job.Direction == TransferDirection.Upload
                ? await FileTransferClient.UploadAsync(_connection, job.SourcePath, job.DestinationDirectory, job.FileName, job.ConflictPolicy, job, run.Token).ConfigureAwait(false)
                : await FileTransferClient.DownloadAsync(_connection, job.SourcePath, job.DestinationDirectory, job.FileName, job.ConflictPolicy, job, run.Token).ConfigureAwait(false);
            job.SetState(outcome == TransferOutcome.Skipped ? TransferState.Skipped : TransferState.Completed);
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            switch (job.Stop)
            {
                case TransferJob.StopReason.Cancel:
                    job.SetState(TransferState.Cancelled);
                    await DiscardPartialAsync(job).ConfigureAwait(false);
                    break;
                default:
                    job.SetState(TransferState.Paused);
                    break;
            }
        }
        catch (FileOperationException ex)
        {
            job.SetState(TransferState.Failed, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            job.SetState(TransferState.Failed, ex.Message);
        }
        finally
        {
            if (acquired)
            {
                _slots.Release();
            }

            job.RunToken = null;
            run.Dispose();
        }
    }

    // The partial file belongs to the receiving side: here for downloads, on the remote computer for uploads.
    private async Task DiscardPartialAsync(TransferJob job)
    {
        var partial = job.PartialPath;
        if (partial.Length == 0)
        {
            return;
        }

        var side = job.Direction == TransferDirection.Download ? _local : _remote;
        // The receiver may still be unwinding and closing its stream after cancellation; retry briefly if transiently locked.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                await side.DeleteAsync(partial, recursive: false, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (FileOperationException ex) when (ex.Error is FileError.Io or FileError.AccessDenied)
            {
                await Task.Delay(50 * (attempt + 1), CancellationToken.None).ConfigureAwait(false);
            }
            catch (FileOperationException)
            {
                // Already gone (NotFound) or not allowed.
                return;
            }
        }
    }
}
