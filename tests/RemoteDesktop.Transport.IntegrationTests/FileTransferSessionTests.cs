using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.File;
using RemoteDesktop.Abstractions.Agent;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>File browsing and transfer over real sessions, directly and through the host service's agent relay.</summary>
public sealed partial class FileTransferSessionTests(ServerFixture server) : IClassFixture<ServerFixture>, IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("rd-files-").FullName;

    /// <summary>
    /// The file-transfer layer still assumes a Windows host, in two places that the tests below trip over:
    /// <c>RemoteFileSource.Combine</c> joins with a literal backslash ("Hosts are Windows in this phase"), and
    /// <see cref="FileAccessPolicy"/> compares an allowed root that was <em>not</em> link-resolved against a path that
    /// was, so any root under a symlink — every macOS temp path, since /var links to /private/var — is refused as
    /// "not shared". Both are P7 in docs/macos-port-plan.md. The rest of the class is platform independent and keeps
    /// running everywhere.
    /// </summary>
    private const string WindowsHostReason =
        "The file-transfer layer assumes a Windows host: backslash path joining and Windows root semantics (P7).";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Folders_can_be_browsed_created_renamed_and_deleted_remotely()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsHostReason);

        await using var pair = await Pair.StartAsync(server, SessionMode.Control);
        await using var remote = new RemoteFileSource(pair.Session.Context.Connection);
        var folder = Path.Combine(_root, "host");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));

        var listing = await remote.ListAsync(folder, Ct);
        Assert.Equal(["sub", "a.txt"], listing.Entries.Select(e => e.Name)); // folders first
        Assert.Equal(5, listing.Entries.Single(e => e.Name == "a.txt").Size);

        var created = remote.Combine(folder, "new");
        await remote.MakeDirectoryAsync(created, Ct);
        Assert.True(Directory.Exists(created));
        await remote.RenameAsync(created, "renamed", Ct);
        Assert.True(Directory.Exists(Path.Combine(folder, "renamed")));
        await remote.DeleteAsync(Path.Combine(folder, "renamed"), recursive: false, Ct);
        Assert.False(Directory.Exists(Path.Combine(folder, "renamed")));

        var missing = await Assert.ThrowsAsync<FileOperationException>(() => remote.ListAsync(Path.Combine(folder, "nope"), Ct));
        Assert.Equal(FileError.NotFound, missing.Error);
        var relative = await Assert.ThrowsAsync<FileOperationException>(() => remote.ListAsync(@"..\..\windows", Ct));
        Assert.Equal(FileError.InvalidPath, relative.Error);

        Assert.NotEmpty((await remote.GetPlacesAsync(Ct)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3 * 1024 * 1024 + 123)]
    public async Task Files_round_trip_with_matching_content(int size)
    {
        await using var pair = await Pair.StartAsync(server, SessionMode.Control);
        var source = Path.Combine(_root, "source.bin");
        File.WriteAllBytes(source, RandomBytes(size, seed: 1));
        var hostFolder = Directory.CreateDirectory(Path.Combine(_root, "host")).FullName;
        var controllerFolder = Directory.CreateDirectory(Path.Combine(_root, "controller")).FullName;

        var up = new Observer();
        Assert.Equal(TransferOutcome.Completed, await FileTransferClient.UploadAsync(pair.Session.Context.Connection, source, hostFolder, "up.bin", ConflictPolicy.Overwrite, up, Ct));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(hostFolder, "up.bin")));
        Assert.Equal(size, up.Last);
        Assert.Empty(Directory.GetFiles(hostFolder, "*" + PartialFile.Extension));
        // Times travel in milliseconds.
        Assert.Equal(new DateTimeOffset(File.GetLastWriteTimeUtc(source)).ToUnixTimeMilliseconds(), new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(hostFolder, "up.bin"))).ToUnixTimeMilliseconds());

        var down = new Observer();
        Assert.Equal(TransferOutcome.Completed, await FileTransferClient.DownloadAsync(pair.Session.Context.Connection, Path.Combine(hostFolder, "up.bin"), controllerFolder, "down.bin", ConflictPolicy.Overwrite, down, Ct));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(controllerFolder, "down.bin")));
        Assert.Equal(size, down.Last);
        Assert.Empty(Directory.GetFiles(controllerFolder, "*" + PartialFile.Extension));
    }

    [Fact]
    public async Task An_interrupted_upload_continues_where_it_stopped()
    {
        await using var pair = await Pair.StartAsync(server, SessionMode.Control);
        var content = RandomBytes(5 * 1024 * 1024, seed: 2);
        var source = Path.Combine(_root, "big.bin");
        File.WriteAllBytes(source, content);
        var hostFolder = Directory.CreateDirectory(Path.Combine(_root, "host")).FullName;

        // What an earlier attempt left behind: the first 2 MiB, under the name the host derives from size and time.
        var final = Path.Combine(hostFolder, "big.bin");
        var partial = PartialFile.PathFor(final, content.Length, new DateTimeOffset(File.GetLastWriteTimeUtc(source)));
        File.WriteAllBytes(partial, content[..(2 * 1024 * 1024)]);

        var observer = new Observer();
        await FileTransferClient.UploadAsync(pair.Session.Context.Connection, source, hostFolder, "big.bin", ConflictPolicy.Overwrite, observer, Ct);

        Assert.Equal(2 * 1024 * 1024, observer.StartOffset);
        Assert.Equal(content, File.ReadAllBytes(final));
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public async Task An_interrupted_download_continues_where_it_stopped()
    {
        await using var pair = await Pair.StartAsync(server, SessionMode.Control);
        var content = RandomBytes(5 * 1024 * 1024, seed: 3);
        var hostFolder = Directory.CreateDirectory(Path.Combine(_root, "host")).FullName;
        var controllerFolder = Directory.CreateDirectory(Path.Combine(_root, "controller")).FullName;
        var remote = Path.Combine(hostFolder, "big.bin");
        File.WriteAllBytes(remote, content);

        var final = Path.Combine(controllerFolder, "big.bin");
        var partial = PartialFile.PathFor(final, content.Length, new DateTimeOffset(File.GetLastWriteTimeUtc(remote)));
        File.WriteAllBytes(partial, content[..(3 * 1024 * 1024 + 5)]);

        var observer = new Observer();
        await FileTransferClient.DownloadAsync(pair.Session.Context.Connection, remote, controllerFolder, "big.bin", ConflictPolicy.Overwrite, observer, Ct);

        Assert.Equal(3 * 1024 * 1024 + 5, observer.StartOffset);
        Assert.Equal(content, File.ReadAllBytes(final));
    }

    [Fact]
    public async Task A_damaged_partial_is_detected_and_the_transfer_starts_over()
    {
        await using var pair = await Pair.StartAsync(server, SessionMode.Control);
        var content = RandomBytes(3 * 1024 * 1024, seed: 4);
        var source = Path.Combine(_root, "src.bin");
        File.WriteAllBytes(source, content);
        var hostFolder = Directory.CreateDirectory(Path.Combine(_root, "host")).FullName;
        var controllerFolder = Directory.CreateDirectory(Path.Combine(_root, "controller")).FullName;

        var wrong = content[..(1024 * 1024)];
        wrong[1000] ^= 0xFF;

        // Upload: the host's partial has a flipped byte.
        var upPartial = PartialFile.PathFor(Path.Combine(hostFolder, "x.bin"), content.Length, new DateTimeOffset(File.GetLastWriteTimeUtc(source)));
        File.WriteAllBytes(upPartial, wrong);
        var up = new Observer();
        await FileTransferClient.UploadAsync(pair.Session.Context.Connection, source, hostFolder, "x.bin", ConflictPolicy.Overwrite, up, Ct);
        Assert.Equal(2, up.Starts.Count);
        Assert.Equal(1024 * 1024, up.Starts[0]);
        Assert.Equal(0, up.Starts[1]);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(hostFolder, "x.bin")));

        // Download: the controller's partial has a flipped byte.
        var downPartial = PartialFile.PathFor(Path.Combine(controllerFolder, "y.bin"), content.Length, new DateTimeOffset(File.GetLastWriteTimeUtc(source)));
        File.WriteAllBytes(downPartial, wrong);
        var down = new Observer();
        await FileTransferClient.DownloadAsync(pair.Session.Context.Connection, source, controllerFolder, "y.bin", ConflictPolicy.Overwrite, down, Ct);
        Assert.Equal([1024 * 1024, 0], down.Starts);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(controllerFolder, "y.bin")));
    }

    [Fact]
    public async Task Conflict_policies_overwrite_skip_or_keep_both()
    {
        await using var pair = await Pair.StartAsync(server, SessionMode.Control);
        var source = Path.Combine(_root, "doc.txt");
        File.WriteAllText(source, "new");
        var hostFolder = Directory.CreateDirectory(Path.Combine(_root, "host")).FullName;
        var existing = Path.Combine(hostFolder, "doc.txt");
        File.WriteAllText(existing, "old");
        var connection = pair.Session.Context.Connection;

        Assert.Equal(TransferOutcome.Skipped, await FileTransferClient.UploadAsync(connection, source, hostFolder, "doc.txt", ConflictPolicy.Skip, new Observer(), Ct));
        Assert.Equal("old", File.ReadAllText(existing));

        Assert.Equal(TransferOutcome.Completed, await FileTransferClient.UploadAsync(connection, source, hostFolder, "doc.txt", ConflictPolicy.Rename, new Observer(), Ct));
        Assert.Equal("old", File.ReadAllText(existing));
        Assert.Equal("new", File.ReadAllText(Path.Combine(hostFolder, "doc (1).txt")));

        Assert.Equal(TransferOutcome.Completed, await FileTransferClient.UploadAsync(connection, source, hostFolder, "doc.txt", ConflictPolicy.Overwrite, new Observer(), Ct));
        Assert.Equal("new", File.ReadAllText(existing));

        // The same three choices when downloading.
        var controllerFolder = Directory.CreateDirectory(Path.Combine(_root, "controller")).FullName;
        File.WriteAllText(Path.Combine(controllerFolder, "doc.txt"), "mine");
        Assert.Equal(TransferOutcome.Skipped, await FileTransferClient.DownloadAsync(connection, existing, controllerFolder, "doc.txt", ConflictPolicy.Skip, new Observer(), Ct));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(controllerFolder, "doc.txt")));
        await FileTransferClient.DownloadAsync(connection, existing, controllerFolder, "doc.txt", ConflictPolicy.Rename, new Observer(), Ct);
        Assert.Equal("new", File.ReadAllText(Path.Combine(controllerFolder, "doc (1).txt")));
        await FileTransferClient.DownloadAsync(connection, existing, controllerFolder, "doc.txt", ConflictPolicy.Overwrite, new Observer(), Ct);
        Assert.Equal("new", File.ReadAllText(Path.Combine(controllerFolder, "doc.txt")));
    }

    [Fact]
    public async Task Missing_or_unwritable_targets_fail_with_a_reason()
    {
        await using var pair = await Pair.StartAsync(server, SessionMode.Control);
        var connection = pair.Session.Context.Connection;
        var source = Path.Combine(_root, "s.txt");
        File.WriteAllText(source, "x");

        var noFolder = await Assert.ThrowsAsync<FileOperationException>(() =>
            FileTransferClient.UploadAsync(connection, source, Path.Combine(_root, "absent"), "s.txt", ConflictPolicy.Overwrite, new Observer(), Ct));
        Assert.Equal(FileError.NotFound, noFolder.Error);

        // The destination is one name, not a path, and the character that would make it a path depends on the host.
        var badName = await Assert.ThrowsAsync<FileOperationException>(() =>
            FileTransferClient.UploadAsync(connection, source, _root, OperatingSystem.IsWindows() ? @"..\evil.txt" : "../evil.txt", ConflictPolicy.Overwrite, new Observer(), Ct));
        Assert.Equal(FileError.InvalidPath, badName.Error);

        var noFile = await Assert.ThrowsAsync<FileOperationException>(() =>
            FileTransferClient.DownloadAsync(connection, Path.Combine(_root, "gone.txt"), _root, "gone.txt", ConflictPolicy.Overwrite, new Observer(), Ct));
        Assert.Equal(FileError.NotFound, noFile.Error);
    }

    [Fact]
    public async Task The_host_shares_only_what_its_policy_allows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsHostReason);

        var shared = Directory.CreateDirectory(Path.Combine(_root, "shared")).FullName;
        var secret = Directory.CreateDirectory(Path.Combine(_root, "secret")).FullName;
        File.WriteAllText(Path.Combine(secret, "key.txt"), "secret");
        await using var pair = await Pair.StartAsync(server, SessionMode.Control, new FileAccessPolicy([shared]));
        await using var remote = new RemoteFileSource(pair.Session.Context.Connection);

        var denied = await Assert.ThrowsAsync<FileOperationException>(() => remote.ListAsync(secret, Ct));
        Assert.Equal(FileError.NotAllowed, denied.Error);
        var traversal = await Assert.ThrowsAsync<FileOperationException>(() => remote.ListAsync(Path.Combine(shared, "..", "secret"), Ct));
        Assert.Equal(FileError.NotAllowed, traversal.Error);
        var download = await Assert.ThrowsAsync<FileOperationException>(() =>
            FileTransferClient.DownloadAsync(pair.Session.Context.Connection, Path.Combine(secret, "key.txt"), _root, "stolen.txt", ConflictPolicy.Overwrite, new Observer(), Ct));
        Assert.Equal(FileError.NotAllowed, download.Error);
        Assert.False(File.Exists(Path.Combine(_root, "stolen.txt")));
        Assert.Empty((await remote.ListAsync(shared, Ct)).Entries);
    }

    [Fact]
    public async Task Nothing_is_served_while_the_host_can_only_act_as_the_system()
    {
        var guard = new SwitchableGuard { Reason = "Files are not available on the login screen." };
        await using var pair = await Pair.StartAsync(server, SessionMode.Control, guard: guard);
        await using var remote = new RemoteFileSource(pair.Session.Context.Connection);

        var refused = await Assert.ThrowsAsync<FileOperationException>(() => remote.ListAsync(_root, Ct));
        Assert.Equal(FileError.NotAllowed, refused.Error);
        var upload = await Assert.ThrowsAsync<FileOperationException>(() =>
            FileTransferClient.UploadAsync(pair.Session.Context.Connection, Write("x.txt"), _root, "x.txt", ConflictPolicy.Overwrite, new Observer(), Ct));
        Assert.Equal(FileError.NotAllowed, upload.Error);

        guard.Reason = null;
        Assert.NotNull(await remote.ListAsync(_root, Ct));
    }

    [Fact]
    public async Task Watch_mode_grants_no_file_access()
    {
        await using var pair = await Pair.StartAsync(server, SessionMode.View);
        Assert.False(pair.Session.Context.Grant.Allows(Permissions.FileTransfer));

        // A modified controller opens the channel anyway; the host closes it.
        await using var remote = new RemoteFileSource(pair.Session.Context.Connection);
        await Assert.ThrowsAsync<FileOperationException>(() => remote.ListAsync(_root, Ct));
        await Assert.ThrowsAsync<FileOperationException>(() =>
            FileTransferClient.UploadAsync(pair.Session.Context.Connection, Write("w.txt"), _root, "w.txt", ConflictPolicy.Overwrite, new Observer(), Ct));
        Assert.False(File.Exists(Path.Combine(_root, "w.txt.copy")));
    }

    [Fact]
    public async Task Large_files_pass_through_the_service_relay_in_both_directions()
    {
        using var gateway = new InProcessAgentGateway([new FileTransferModule(new SwitchableGuard(), FileAccessPolicy.Unrestricted, NullLogger<FileTransferModule>.Instance)]);
        await using var pair = await Pair.StartAsync(server, SessionMode.Control, relay: gateway);
        var content = RandomBytes(24 * 1024 * 1024 + 7, seed: 5);
        var source = Path.Combine(_root, "large.bin");
        File.WriteAllBytes(source, content);
        var hostFolder = Directory.CreateDirectory(Path.Combine(_root, "host")).FullName;
        var controllerFolder = Directory.CreateDirectory(Path.Combine(_root, "controller")).FullName;
        var connection = pair.Session.Context.Connection;

        await FileTransferClient.UploadAsync(connection, source, hostFolder, "large.bin", ConflictPolicy.Overwrite, new Observer(), Ct);
        Assert.Equal(content.Length, new FileInfo(Path.Combine(hostFolder, "large.bin")).Length);
        await FileTransferClient.DownloadAsync(connection, Path.Combine(hostFolder, "large.bin"), controllerFolder, "large.bin", ConflictPolicy.Overwrite, new Observer(), Ct);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(controllerFolder, "large.bin")));

        await using var remote = new RemoteFileSource(connection);
        Assert.Contains((await remote.ListAsync(hostFolder, Ct)).Entries, e => e.Name == "large.bin");
    }

    [Fact]
    public async Task A_lost_agent_ends_a_transfer_instead_of_corrupting_it_and_the_next_attempt_resumes()
    {
        using var gateway = new InProcessAgentGateway([new FileTransferModule(new SwitchableGuard(), FileAccessPolicy.Unrestricted, NullLogger<FileTransferModule>.Instance)]);
        await using var pair = await Pair.StartAsync(server, SessionMode.Control, relay: gateway);
        var content = RandomBytes(40 * 1024 * 1024, seed: 6);
        var source = Path.Combine(_root, "long.bin");
        File.WriteAllBytes(source, content);
        var hostFolder = Directory.CreateDirectory(Path.Combine(_root, "host")).FullName;
        var connection = pair.Session.Context.Connection;

        var observer = new Observer();
        var reachedMiddle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        observer.OnProgress = bytes =>
        {
            if (bytes > 8 * 1024 * 1024)
            {
                reachedMiddle.TrySetResult();
            }
        };

        var first = FileTransferClient.UploadAsync(connection, source, hostFolder, "long.bin", ConflictPolicy.Overwrite, observer, Ct);
        await reachedMiddle.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await gateway.RestartAgentAsync();
        await Assert.ThrowsAsync<FileOperationException>(() => first);
        Assert.False(File.Exists(Path.Combine(hostFolder, "long.bin"))); // never visible half-written

        var second = new Observer();
        await FileTransferClient.UploadAsync(connection, source, hostFolder, "long.bin", ConflictPolicy.Overwrite, second, Ct);
        Assert.True(second.StartOffset > 0, "the second attempt should continue, not start over");
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(hostFolder, "long.bin")));
    }

    private string Write(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "content");
        return path;
    }

    private static byte[] RandomBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private sealed class SwitchableGuard : IUserContextGuard
    {
        public volatile string? Reason;

        public string? GetRefusalReason() => Reason;
    }

    private sealed class Observer : ITransferObserver
    {
        public List<long> Starts { get; } = [];

        public long StartOffset => Starts.Count == 0 ? -1 : Starts[^1];

        public long Last { get; private set; }

        public Action<long>? OnProgress { get; set; }

        public void Started(long totalBytes, long startOffset, string finalPath, string partialPath) => Starts.Add(startOffset);

        public void Progress(long bytes)
        {
            Last = bytes;
            OnProgress?.Invoke(bytes);
        }
    }

    /// <summary>A host that serves files and a controller connected to it.</summary>
    private sealed class Pair : IAsyncDisposable
    {
        private TestDevice _host = null!;
        private TestDevice _controller = null!;

        public RemoteSession Session { get; private set; } = null!;

        public static async Task<Pair> StartAsync(
            ServerFixture server,
            SessionMode mode,
            FileAccessPolicy? policy = null,
            IUserContextGuard? guard = null,
            InProcessAgentGateway? relay = null)
        {
            var pair = new Pair();
            IFeatureModule module = relay is null
                ? new FileTransferModule(guard ?? new SwitchableGuard(), policy ?? FileAccessPolicy.Unrestricted, NullLogger<FileTransferModule>.Instance)
                : new AgentForwardingModule(ChannelType.FileTransfer, Permissions.FileTransfer, AgentChannelKind.File, false, relay,
                    NullLogger<AgentForwardingModule>.Instance, maxInboundFrame: 2 * 1024 * 1024, lossless: true);
            pair._host = await TestDevice.StartAsync(server, forceRelay: false, module);
            pair._controller = await TestDevice.StartAsync(server);
            pair.Session = await pair._controller.Controller.ConnectAsync(pair._host.Id, pair._host.Credentials.TemporaryPassword, mode, Ct);
            return pair;
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await _controller.DisposeAsync();
            await _host.DisposeAsync();
        }
    }
}
