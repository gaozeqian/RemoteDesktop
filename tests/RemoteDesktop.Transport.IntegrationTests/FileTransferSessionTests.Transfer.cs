using RemoteDesktop.Domain;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.File;
using RemoteDesktop.TestSupport;

namespace RemoteDesktop.Transport.IntegrationTests;

// The transfer list: queueing, folder trees, pausing, cancelling and retrying over a real session.
public sealed partial class FileTransferSessionTests
{
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Folder_trees_are_recreated_at_the_destination_in_both_directions()
    {
        await using var pair = await Pair.StartAsync(server, SessionMode.Control);
        await using var remote = new RemoteFileSource(pair.Session.Context.Connection);
        await using var manager = new TransferManager(pair.Session.Context.Connection, new LocalFileSource(), remote);

        var tree = Directory.CreateDirectory(Path.Combine(_root, "local", "project")).FullName;
        Directory.CreateDirectory(Path.Combine(tree, "src", "deep"));
        Directory.CreateDirectory(Path.Combine(tree, "empty"));
        File.WriteAllBytes(Path.Combine(tree, "readme.md"), RandomBytes(100, 1));
        File.WriteAllBytes(Path.Combine(tree, "src", "a.cs"), RandomBytes(5000, 2));
        File.WriteAllBytes(Path.Combine(tree, "src", "deep", "b.cs"), RandomBytes(2 * 1024 * 1024, 3));
        var hostFolder = Directory.CreateDirectory(Path.Combine(_root, "host")).FullName;

        var items = (await new LocalFileSource().ListAsync(Path.GetDirectoryName(tree)!, Ct)).Entries;
        var uploads = await manager.EnqueueAsync(TransferDirection.Upload, Path.GetDirectoryName(tree)!, items, hostFolder, ConflictPolicy.Overwrite, Ct);
        Assert.Equal(3, uploads.Count);
        await manager.WhenIdleAsync(Ct);
        Assert.All(uploads, j => Assert.Equal(TransferState.Completed, j.State));
        Assert.Equal(File.ReadAllBytes(Path.Combine(tree, "src", "deep", "b.cs")), File.ReadAllBytes(Path.Combine(hostFolder, "project", "src", "deep", "b.cs")));
        Assert.True(Directory.Exists(Path.Combine(hostFolder, "project", "empty")));

        var back = Directory.CreateDirectory(Path.Combine(_root, "back")).FullName;
        var remoteItems = (await remote.ListAsync(hostFolder, Ct)).Entries;
        var downloads = await manager.EnqueueAsync(TransferDirection.Download, hostFolder, remoteItems, back, ConflictPolicy.Overwrite, Ct);
        await manager.WhenIdleAsync(Ct);
        Assert.Equal(3, downloads.Count);
        Assert.All(downloads, j => Assert.Equal(TransferState.Completed, j.State));
        Assert.Equal(File.ReadAllBytes(Path.Combine(tree, "src", "a.cs")), File.ReadAllBytes(Path.Combine(back, "project", "src", "a.cs")));
        Assert.True(Directory.Exists(Path.Combine(back, "project", "empty")));
    }

    [Fact]
    public async Task A_paused_transfer_keeps_its_progress_and_resumes_to_the_same_bytes()
    {
        await using var pair = await Pair.StartAsync(server, SessionMode.Control);
        await using var remote = new RemoteFileSource(pair.Session.Context.Connection);
        await using var manager = new TransferManager(pair.Session.Context.Connection, new LocalFileSource(), remote);
        var content = RandomBytes(128 * 1024 * 1024, 8);
        var source = Path.Combine(_root, "local", "huge.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, content);
        var hostFolder = Directory.CreateDirectory(Path.Combine(_root, "host")).FullName;

        var job = Assert.Single(await manager.EnqueueAsync(TransferDirection.Upload, Path.GetDirectoryName(source)!, [Entry(source)], hostFolder, ConflictPolicy.Overwrite, Ct));
        await WaitUntilAsync(() => job.TransferredBytes > 8 * 1024 * 1024 || !job.IsActive);
        manager.Pause(job);
        await WaitUntilAsync(() => job.State == TransferState.Paused);
        var kept = job.TransferredBytes;
        Assert.InRange(kept, 1, content.Length - 1);
        Assert.False(File.Exists(Path.Combine(hostFolder, "huge.bin")));
        Assert.Single(Directory.GetFiles(hostFolder, "*" + PartialFile.Extension));

        manager.Resume(job);
        await manager.WhenIdleAsync(Ct);
        Assert.Equal(TransferState.Completed, job.State);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(hostFolder, "huge.bin")));
        Assert.Empty(Directory.GetFiles(hostFolder, "*" + PartialFile.Extension));
    }

    [Theory]
    [InlineData(TransferDirection.Upload)]
    [InlineData(TransferDirection.Download)]
    public async Task Cancelling_removes_the_partial_file(TransferDirection direction)
    {
        await using var pair = await Pair.StartAsync(server, SessionMode.Control);
        await using var remote = new RemoteFileSource(pair.Session.Context.Connection);
        await using var manager = new TransferManager(pair.Session.Context.Connection, new LocalFileSource(), remote);
        var localFolder = Directory.CreateDirectory(Path.Combine(_root, "local")).FullName;
        var hostFolder = Directory.CreateDirectory(Path.Combine(_root, "host")).FullName;
        var file = direction == TransferDirection.Upload ? Path.Combine(localFolder, "c.bin") : Path.Combine(hostFolder, "c.bin");
        File.WriteAllBytes(file, RandomBytes(128 * 1024 * 1024, 9));
        var receiving = direction == TransferDirection.Upload ? hostFolder : localFolder;
        var (from, to) = direction == TransferDirection.Upload ? (localFolder, hostFolder) : (hostFolder, localFolder);

        var job = Assert.Single(await manager.EnqueueAsync(direction, from, [Entry(file)], to, ConflictPolicy.Overwrite, Ct));
        await WaitUntilAsync(() => job.TransferredBytes > 8 * 1024 * 1024 || !job.IsActive);
        manager.Cancel(job);
        await manager.WhenIdleAsync(Ct);

        Assert.Equal(TransferState.Cancelled, job.State);
        await WaitUntilAsync(() => Directory.GetFiles(receiving, "*" + PartialFile.Extension).Length == 0);
        Assert.False(File.Exists(Path.Combine(receiving, "c.bin")));
    }

    [Fact]
    public async Task A_failed_transfer_can_be_retried_once_the_problem_is_fixed()
    {
        await using var pair = await Pair.StartAsync(server, SessionMode.Control);
        await using var remote = new RemoteFileSource(pair.Session.Context.Connection);
        await using var manager = new TransferManager(pair.Session.Context.Connection, new LocalFileSource(), remote);
        var source = Write("retry.txt");
        var target = Path.Combine(_root, "not-yet");

        var job = Assert.Single(await manager.EnqueueAsync(TransferDirection.Upload, _root, [Entry(source)], target, ConflictPolicy.Overwrite, Ct));
        await manager.WhenIdleAsync(Ct);
        Assert.Equal(TransferState.Failed, job.State);
        Assert.False(string.IsNullOrEmpty(job.Error));

        Directory.CreateDirectory(target);
        manager.Resume(job);
        await manager.WhenIdleAsync(Ct);
        Assert.Equal(TransferState.Completed, job.State);
        Assert.Null(job.Error);
        Assert.Equal("content", File.ReadAllText(Path.Combine(target, "retry.txt")));
    }

    [Fact]
    public async Task Only_the_allowed_number_of_transfers_run_at_once()
    {
        await using var pair = await Pair.StartAsync(server, SessionMode.Control);
        await using var remote = new RemoteFileSource(pair.Session.Context.Connection);
        await using var manager = new TransferManager(pair.Session.Context.Connection, new LocalFileSource(), remote, maxConcurrent: 2);
        var localFolder = Directory.CreateDirectory(Path.Combine(_root, "local")).FullName;
        var hostFolder = Directory.CreateDirectory(Path.Combine(_root, "host")).FullName;
        var entries = new List<FileEntryInfo>();
        for (var i = 0; i < 5; i++)
        {
            var path = Path.Combine(localFolder, $"f{i}.bin");
            File.WriteAllBytes(path, RandomBytes(16 * 1024 * 1024, 20 + i));
            entries.Add(Entry(path));
        }

        var jobs = await manager.EnqueueAsync(TransferDirection.Upload, localFolder, entries, hostFolder, ConflictPolicy.Overwrite, Ct);
        var deadline = DateTime.UtcNow + JobTimeout;
        var peak = 0;
        while (jobs.Any(j => j.IsActive))
        {
            peak = Math.Max(peak, jobs.Count(j => j.State == TransferState.Running));
            Assert.True(DateTime.UtcNow < deadline, $"the transfers never finished; still active: {jobs.Count(j => j.IsActive)}");
            await Task.Delay(2, Ct);
        }

        Assert.InRange(peak, 1, 2);
        Assert.All(jobs, j => Assert.Equal(TransferState.Completed, j.State));
        Assert.Equal(5, Directory.GetFiles(hostFolder).Length);
    }

    private static FileEntryInfo Entry(string path)
    {
        var info = new FileInfo(path);
        return new FileEntryInfo(info.Name, IsDirectory: false, info.Length, info.LastWriteTimeUtc, FileEntryAttributes.None);
    }

    private static Task WaitUntilAsync(Func<bool> condition) =>
        TestWait.UntilAsync(condition, "the transfer job", JobTimeout, TimeSpan.FromMilliseconds(2));
}
