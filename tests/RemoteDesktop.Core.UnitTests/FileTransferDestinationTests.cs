using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.File;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The receiving side of a download is this computer, and the names it is asked to create come from the other one.
/// A listing is a message, not a promise: a malicious host can call a folder "../../.ssh" and ship files inside it.
/// </summary>
public class FileTransferDestinationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_remote_directory_name_that_climbs_out_of_the_destination_is_refused()
    {
        var local = new FakeSource();
        var remote = new FakeSource();
        var manager = new TransferManager(new FakeConnection(), local, remote);

        var jobs = await manager.EnqueueAsync(
            TransferDirection.Download,
            "/remote",
            [new FileEntryInfo("../../.ssh", IsDirectory: true, 0, DateTimeOffset.UnixEpoch, FileEntryAttributes.None)],
            "/home/user/Downloads",
            ConflictPolicy.Overwrite,
            Ct);

        var job = Assert.Single(jobs);
        Assert.Equal(TransferState.Failed, job.State);
        Assert.Empty(local.DirectoriesCreated);
    }

    [Fact]
    public async Task A_remote_child_directory_name_that_climbs_out_is_refused_too()
    {
        var local = new FakeSource();
        var remote = new FakeSource();
        remote.Listings[remote.Combine("/remote", "Documents")] =
        [
            new FileEntryInfo("..", IsDirectory: true, 0, DateTimeOffset.UnixEpoch, FileEntryAttributes.None),
        ];

        var manager = new TransferManager(new FakeConnection(), local, remote);

        var jobs = await manager.EnqueueAsync(
            TransferDirection.Download,
            "/remote",
            [new FileEntryInfo("Documents", IsDirectory: true, 0, DateTimeOffset.UnixEpoch, FileEntryAttributes.None)],
            "/home/user/Downloads",
            ConflictPolicy.Overwrite,
            Ct);

        Assert.All(local.DirectoriesCreated, path => Assert.DoesNotContain("..", path, StringComparison.Ordinal));
        Assert.Contains(jobs, job => job.State == TransferState.Failed);
    }

    /// <summary>A side of the file manager that never touches disk, for looking at the paths the manager asks for.</summary>
    private sealed class FakeSource : IFileSource
    {
        public Dictionary<string, IReadOnlyList<FileEntryInfo>> Listings { get; } = new(StringComparer.Ordinal);

        public List<string> DirectoriesCreated { get; } = [];

        public Task<DirectoryContents> ListAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new DirectoryContents(
                path,
                string.Empty,
                Listings.TryGetValue(path, out var entries) ? entries : [],
                Truncated: false));

        public Task<IReadOnlyList<PlaceInfo>> GetPlacesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PlaceInfo>>([]);

        public Task MakeDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            DirectoriesCreated.Add(path);
            return Task.CompletedTask;
        }

        public Task RenameAsync(string path, string newName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken) => Task.CompletedTask;

        public string Combine(string folder, string name) => Path.Combine(folder, name);
    }

    /// <summary>No channel is opened by these tests: every name is refused before a transfer starts.</summary>
    private sealed class FakeConnection : IPeerConnection
    {
        public RemoteDesktop.Domain.DeviceId RemoteDevice => default;

        public ConnectionPath Path => ConnectionPath.Direct;

        public string? RelaySessionId => null;

        public string LocalFingerprint => string.Empty;

        public string RemoteFingerprint => string.Empty;

        public ValueTask<IChannel> OpenChannelAsync(ChannelType type, bool bidirectional, CancellationToken cancellationToken) =>
            throw new NotSupportedException("No channel should be opened for a refused name.");

        public ValueTask<IChannel?> AcceptChannelAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("No channel should be accepted for a refused name.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
