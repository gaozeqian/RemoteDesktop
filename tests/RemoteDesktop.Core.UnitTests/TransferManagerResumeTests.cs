using RemoteDesktop.Domain;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.File;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// A job can be resumed from the very notification that announces its failure, which runs before the failed run has
/// finished unwinding. That unwinding must not take the new run's cancellation token with it.
/// </summary>
public sealed class TransferManagerResumeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_run_that_unwinds_after_the_job_was_resumed_leaves_the_new_run_stoppable()
    {
        var connection = new FailingThenWaitingConnection();
        var manager = new TransferManager(connection, new NoSource(), new NoSource());
        CancellationTokenSource? failedRun = null;
        var resumed = 0;
        TransferJob? job = null;
        manager.JobAdded += (_, added) =>
        {
            job = added;
            added.Changed += (_, _) =>
            {
                if (added.State == TransferState.Failed && Interlocked.Exchange(ref resumed, 1) == 0)
                {
                    failedRun = added.RunToken;
                    manager.Resume(added); // before the failed run's cleanup has happened
                }
            };
        };

        await manager.EnqueueAsync(
            TransferDirection.Download,
            "/remote",
            [new FileEntryInfo("file.bin", IsDirectory: false, 10, DateTimeOffset.UnixEpoch, FileEntryAttributes.None)],
            Path.GetTempPath(),
            ConflictPolicy.Overwrite,
            Ct);

        await connection.SecondAttemptWaiting.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.NotNull(failedRun);
        await WaitUntilDisposedAsync(failedRun);

        // The new run is the one in flight; pausing must still reach it.
        manager.Pause(job!);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await manager.WhenIdleAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            // Falls through to the assertion, which names the problem: the run could not be stopped.
        }

        Assert.Equal(TransferState.Paused, job!.State);
    }

    private static async Task WaitUntilDisposedAsync(CancellationTokenSource source)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            try
            {
                _ = source.Token;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            await Task.Delay(5, timeout.Token);
        }
    }

    /// <summary>Fails the first channel, and keeps the second one waiting until its run is cancelled.</summary>
    private sealed class FailingThenWaitingConnection : IPeerConnection
    {
        private int _opened;

        public TaskCompletionSource SecondAttemptWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DeviceId RemoteDevice => default;

        public ConnectionPath Path => ConnectionPath.Direct;

        public string? RelaySessionId => null;

        public string LocalFingerprint => string.Empty;

        public string RemoteFingerprint => string.Empty;

        public async ValueTask<IChannel> OpenChannelAsync(ChannelType type, bool bidirectional, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _opened) == 1)
            {
                throw new IOException("The connection dropped.");
            }

            SecondAttemptWaiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }

        public ValueTask<IChannel?> AcceptChannelAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IChannel?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoSource : IFileSource
    {
        public Task<DirectoryContents> ListAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new DirectoryContents(path, string.Empty, [], Truncated: false));

        public Task<IReadOnlyList<PlaceInfo>> GetPlacesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PlaceInfo>>([]);

        public Task MakeDirectoryAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RenameAsync(string path, string newName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken) => Task.CompletedTask;

        public string Combine(string folder, string name) => System.IO.Path.Combine(folder, name);
    }
}
