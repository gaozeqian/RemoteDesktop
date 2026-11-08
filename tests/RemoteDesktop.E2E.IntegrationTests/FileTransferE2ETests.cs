using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Protocol.File;
using RemoteDesktop.Transport;

namespace RemoteDesktop.E2E.IntegrationTests;

/// <summary>
/// End-to-end integration tests for remote file operations and transfers over live cluster sessions,
/// including directory management, bidirectional transfers, resume on interrupted transfers,
/// checksum validation, conflict resolution, and watch-mode permission guards.
/// </summary>
public sealed class FileTransferE2ETests(EndToEndClusterFixture cluster) : IClassFixture<EndToEndClusterFixture>, IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _testRoot = Directory.CreateTempSubdirectory("rd-e2e-files-").FullName;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task Folders_and_files_can_be_browsed_created_renamed_and_deleted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "RemoteFileSource path joining uses Windows backslashes.");

        var module = new FileTransferModule(
            new PermissiveUserContextGuard(),
            FileAccessPolicy.Unrestricted,
            NullLogger<FileTransferModule>.Instance);

        await using var host = await cluster.StartNodeAsync(new EndToEndNodeOptions { Features = [module] }, Ct);
        await using var controller = await cluster.StartNodeAsync(cancellationToken: Ct);

        await using var session = await controller.ConnectAsync(
            host.Id,
            host.Credentials.TemporaryPassword,
            SessionMode.Control,
            Ct);

        await using var remote = new RemoteFileSource(session.Context.Connection);
        var hostDir = Path.Combine(_testRoot, "host-browse");
        Directory.CreateDirectory(hostDir);
        File.WriteAllText(Path.Combine(hostDir, "item1.txt"), "content-1");
        Directory.CreateDirectory(Path.Combine(hostDir, "subfolder"));

        var listing = await remote.ListAsync(hostDir, Ct);
        Assert.Equal(["subfolder", "item1.txt"], listing.Entries.Select(e => e.Name));

        var newDir = remote.Combine(hostDir, "created-dir");
        await remote.MakeDirectoryAsync(newDir, Ct);
        Assert.True(Directory.Exists(newDir));

        await remote.RenameAsync(newDir, "renamed-dir", Ct);
        var renamedPath = Path.Combine(hostDir, "renamed-dir");
        Assert.True(Directory.Exists(renamedPath));

        await remote.DeleteAsync(renamedPath, recursive: false, Ct);
        Assert.False(Directory.Exists(renamedPath));

        // Negative cases: non-existent folder and relative path traversal
        var notFound = await Assert.ThrowsAsync<FileOperationException>(() =>
            remote.ListAsync(Path.Combine(hostDir, "nonexistent"), Ct));
        Assert.Equal(FileError.NotFound, notFound.Error);

        var traversal = await Assert.ThrowsAsync<FileOperationException>(() =>
            remote.ListAsync(@"..\..\system", Ct));
        Assert.Equal(FileError.InvalidPath, traversal.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(512)]
    [InlineData(2 * 1024 * 1024 + 17)]
    public async Task Files_upload_and_download_round_trip_with_matching_content_and_metadata(int size)
    {
        var module = new FileTransferModule(
            new PermissiveUserContextGuard(),
            FileAccessPolicy.Unrestricted,
            NullLogger<FileTransferModule>.Instance);

        await using var host = await cluster.StartNodeAsync(new EndToEndNodeOptions { Features = [module] }, Ct);
        await using var controller = await cluster.StartNodeAsync(cancellationToken: Ct);

        await using var session = await controller.ConnectAsync(
            host.Id,
            host.Credentials.TemporaryPassword,
            SessionMode.Control,
            Ct);

        var sourceFile = Path.Combine(_testRoot, $"src-{size}.bin");
        var content = RandomBytes(size);
        File.WriteAllBytes(sourceFile, content);

        var hostDir = Directory.CreateDirectory(Path.Combine(_testRoot, "host-dest")).FullName;
        var controllerDir = Directory.CreateDirectory(Path.Combine(_testRoot, "ctrl-dest")).FullName;

        // 1. Upload from Controller to Host
        var upObserver = new TransferProgressObserver();
        var upOutcome = await FileTransferClient.UploadAsync(
            session.Context.Connection,
            sourceFile,
            hostDir,
            $"uploaded-{size}.bin",
            ConflictPolicy.Overwrite,
            upObserver,
            Ct);

        Assert.Equal(TransferOutcome.Completed, upOutcome);
        var uploadedFile = Path.Combine(hostDir, $"uploaded-{size}.bin");
        Assert.True(File.Exists(uploadedFile));
        Assert.Equal(content, File.ReadAllBytes(uploadedFile));
        Assert.Equal(size, upObserver.LastBytes);

        // Verify no leftover partial files
        Assert.Empty(Directory.GetFiles(hostDir, "*" + PartialFile.Extension));

        // 2. Download from Host back to Controller
        var downObserver = new TransferProgressObserver();
        var downOutcome = await FileTransferClient.DownloadAsync(
            session.Context.Connection,
            uploadedFile,
            controllerDir,
            $"downloaded-{size}.bin",
            ConflictPolicy.Overwrite,
            downObserver,
            Ct);

        Assert.Equal(TransferOutcome.Completed, downOutcome);
        var downloadedFile = Path.Combine(controllerDir, $"downloaded-{size}.bin");
        Assert.True(File.Exists(downloadedFile));
        Assert.Equal(content, File.ReadAllBytes(downloadedFile));
        Assert.Equal(size, downObserver.LastBytes);
        Assert.Empty(Directory.GetFiles(controllerDir, "*" + PartialFile.Extension));
    }

    [Fact]
    public async Task Interrupted_upload_and_download_resumes_from_partial_file()
    {
        var module = new FileTransferModule(
            new PermissiveUserContextGuard(),
            FileAccessPolicy.Unrestricted,
            NullLogger<FileTransferModule>.Instance);

        await using var host = await cluster.StartNodeAsync(new EndToEndNodeOptions { Features = [module] }, Ct);
        await using var controller = await cluster.StartNodeAsync(cancellationToken: Ct);

        await using var session = await controller.ConnectAsync(
            host.Id,
            host.Credentials.TemporaryPassword,
            SessionMode.Control,
            Ct);

        const int totalSize = 3 * 1024 * 1024;
        const int existingPartialSize = 1024 * 1024;
        var content = RandomBytes(totalSize);

        var sourceFile = Path.Combine(_testRoot, "resume-source.bin");
        File.WriteAllBytes(sourceFile, content);

        var hostDir = Directory.CreateDirectory(Path.Combine(_testRoot, "host-resume")).FullName;
        var finalDest = Path.Combine(hostDir, "resumed.bin");

        // Pre-create partial file simulating previous interrupted upload
        var partialPath = PartialFile.PathFor(finalDest, content.Length, new DateTimeOffset(File.GetLastWriteTimeUtc(sourceFile)));
        File.WriteAllBytes(partialPath, content[..existingPartialSize]);

        var observer = new TransferProgressObserver();
        var outcome = await FileTransferClient.UploadAsync(
            session.Context.Connection,
            sourceFile,
            hostDir,
            "resumed.bin",
            ConflictPolicy.Overwrite,
            observer,
            Ct);

        Assert.Equal(TransferOutcome.Completed, outcome);
        Assert.Equal(existingPartialSize, observer.StartOffset);
        Assert.Equal(content, File.ReadAllBytes(finalDest));
        Assert.False(File.Exists(partialPath));
    }

    [Fact]
    public async Task Damaged_partial_file_is_detected_and_restarts_from_beginning()
    {
        var module = new FileTransferModule(
            new PermissiveUserContextGuard(),
            FileAccessPolicy.Unrestricted,
            NullLogger<FileTransferModule>.Instance);

        await using var host = await cluster.StartNodeAsync(new EndToEndNodeOptions { Features = [module] }, Ct);
        await using var controller = await cluster.StartNodeAsync(cancellationToken: Ct);

        await using var session = await controller.ConnectAsync(
            host.Id,
            host.Credentials.TemporaryPassword,
            SessionMode.Control,
            Ct);

        const int totalSize = 2 * 1024 * 1024;
        var content = RandomBytes(totalSize);

        var sourceFile = Path.Combine(_testRoot, "corrupt-source.bin");
        File.WriteAllBytes(sourceFile, content);

        var hostDir = Directory.CreateDirectory(Path.Combine(_testRoot, "host-corrupt")).FullName;
        var finalDest = Path.Combine(hostDir, "corrupted.bin");

        // Write a corrupt partial file (flipped byte in chunk)
        var corruptedPartial = content[..(1024 * 1024)];
        corruptedPartial[500] ^= 0xFF;
        var partialPath = PartialFile.PathFor(finalDest, content.Length, new DateTimeOffset(File.GetLastWriteTimeUtc(sourceFile)));
        File.WriteAllBytes(partialPath, corruptedPartial);

        var observer = new TransferProgressObserver();
        var outcome = await FileTransferClient.UploadAsync(
            session.Context.Connection,
            sourceFile,
            hostDir,
            "corrupted.bin",
            ConflictPolicy.Overwrite,
            observer,
            Ct);

        Assert.Equal(TransferOutcome.Completed, outcome);
        // Starts with partial offset, fails checksum, resets to 0
        Assert.Equal(2, observer.Starts.Count);
        Assert.Equal(1024 * 1024, observer.Starts[0]);
        Assert.Equal(0, observer.Starts[1]);
        Assert.Equal(content, File.ReadAllBytes(finalDest));
        Assert.False(File.Exists(partialPath));
    }

    [Fact]
    public async Task Conflict_policies_honor_overwrite_skip_and_keep_both()
    {
        var module = new FileTransferModule(
            new PermissiveUserContextGuard(),
            FileAccessPolicy.Unrestricted,
            NullLogger<FileTransferModule>.Instance);

        await using var host = await cluster.StartNodeAsync(new EndToEndNodeOptions { Features = [module] }, Ct);
        await using var controller = await cluster.StartNodeAsync(cancellationToken: Ct);

        await using var session = await controller.ConnectAsync(
            host.Id,
            host.Credentials.TemporaryPassword,
            SessionMode.Control,
            Ct);

        var hostDir = Directory.CreateDirectory(Path.Combine(_testRoot, "conflict-dir")).FullName;
        var existingFile = Path.Combine(hostDir, "target.txt");
        File.WriteAllText(existingFile, "original-content");

        var newSource = Path.Combine(_testRoot, "source-conflict.txt");
        File.WriteAllText(newSource, "new-content");

        // 1. ConflictPolicy.Skip: should not modify target
        var skipOutcome = await FileTransferClient.UploadAsync(
            session.Context.Connection,
            newSource,
            hostDir,
            "target.txt",
            ConflictPolicy.Skip,
            new TransferProgressObserver(),
            Ct);
        Assert.Equal(TransferOutcome.Skipped, skipOutcome);
        Assert.Equal("original-content", File.ReadAllText(existingFile));

        // 2. ConflictPolicy.Rename (Keep both): creates target (1).txt
        var keepOutcome = await FileTransferClient.UploadAsync(
            session.Context.Connection,
            newSource,
            hostDir,
            "target.txt",
            ConflictPolicy.Rename,
            new TransferProgressObserver(),
            Ct);
        Assert.Equal(TransferOutcome.Completed, keepOutcome);
        var bothFile = Path.Combine(hostDir, "target (1).txt");
        Assert.True(File.Exists(bothFile));
        Assert.Equal("new-content", File.ReadAllText(bothFile));

        // 3. ConflictPolicy.Overwrite: replaces target.txt
        var overwriteOutcome = await FileTransferClient.UploadAsync(
            session.Context.Connection,
            newSource,
            hostDir,
            "target.txt",
            ConflictPolicy.Overwrite,
            new TransferProgressObserver(),
            Ct);
        Assert.Equal(TransferOutcome.Completed, overwriteOutcome);
        Assert.Equal("new-content", File.ReadAllText(existingFile));
    }

    [Fact]
    public async Task Watch_mode_session_is_strictly_forbidden_from_file_transfer()
    {
        var module = new FileTransferModule(
            new PermissiveUserContextGuard(),
            FileAccessPolicy.Unrestricted,
            NullLogger<FileTransferModule>.Instance);

        await using var host = await cluster.StartNodeAsync(new EndToEndNodeOptions { Features = [module] }, Ct);
        await using var controller = await cluster.StartNodeAsync(cancellationToken: Ct);

        // Connect with View-only mode
        await using var session = await controller.ConnectAsync(
            host.Id,
            host.Credentials.TemporaryPassword,
            SessionMode.View,
            Ct);

        // Attempting to open remote file source should fail because FileTransfer channel is disallowed
        await using var remote = new RemoteFileSource(session.Context.Connection);
        await Assert.ThrowsAnyAsync<Exception>(() => remote.ListAsync(_testRoot, Ct));
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private sealed class PermissiveUserContextGuard : IUserContextGuard
    {
        public string? GetRefusalReason() => null;
    }

    private sealed class TransferProgressObserver : ITransferObserver
    {
        public List<long> Starts { get; } = [];

        public long StartOffset => Starts.Count == 0 ? -1 : Starts[^1];

        public long LastBytes { get; private set; }

        public void Started(long totalBytes, long startOffset, string finalPath, string partialPath) =>
            Starts.Add(startOffset);

        public void Progress(long bytes) => LastBytes = bytes;
    }
}
