using System.IO.Hashing;
using System.IO.Pipes;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.File;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The host side of one upload, played over a pipe with the protocol written out by hand. That makes it possible to
/// do what a well-behaved client never does: announce a nonsensical size, or change the destination folder between
/// "accepted" and "finished".
/// </summary>
public sealed class FileTransferModuleTests : IDisposable
{
    private static readonly SessionContext TestSession =
        new(SessionId.New(), SessionRole.Host, null!, AccessGrant.Create(SessionMode.Control, Permissions.All, Permissions.All));

    private readonly string _folder = Directory.CreateTempSubdirectory("rd-module-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_size_that_does_not_fit_a_signed_number_is_refused_before_anything_is_created()
    {
        var upload = Request("huge.bin", size: ulong.MaxValue, ConflictPolicy.Overwrite);

        var result = await UploadAsync(upload, content: []);

        Assert.Equal(FileResponse.BodyOneofCase.Result, result.First.BodyCase);
        Assert.Equal(FileError.InvalidPath, result.First.Result.Status.Error);
        Assert.Empty(Directory.GetFileSystemEntries(_folder));
    }

    [Fact]
    public async Task A_new_upload_lands_under_its_name_and_the_partial_is_gone()
    {
        var content = Bytes(5000);

        var result = await UploadAsync(Request("new.bin", (ulong)content.Length, ConflictPolicy.Overwrite), content);

        Assert.Equal(FileError.None, result.Complete!.Complete.Status.Error);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(_folder, "new.bin"), Ct));
        Assert.Equal(["new.bin"], Names());
    }

    [Fact]
    public async Task Overwriting_replaces_the_file_and_leaves_no_temporary_behind()
    {
        var path = Path.Combine(_folder, "doc.txt");
        await File.WriteAllTextAsync(path, "old", Ct);
        var content = Bytes(3000);

        var result = await UploadAsync(Request("doc.txt", (ulong)content.Length, ConflictPolicy.Overwrite), content);

        Assert.Equal(FileError.None, result.Complete!.Complete.Status.Error);
        Assert.Equal(content, await File.ReadAllBytesAsync(path, Ct));
        Assert.Equal(["doc.txt"], Names());
    }

    [Fact]
    public async Task A_name_taken_while_the_upload_was_running_gets_the_next_free_name_instead_of_failing()
    {
        var content = Bytes(2000);
        var taken = Path.Combine(_folder, "report.txt");

        // The name is free when the upload is accepted and taken by somebody else before it is finished.
        var result = await UploadAsync(
            Request("report.txt", (ulong)content.Length, ConflictPolicy.Rename),
            content,
            afterAccepted: _ => File.WriteAllText(taken, "somebody else's"));

        Assert.Equal(FileError.None, result.Complete!.Complete.Status.Error);
        Assert.Equal("somebody else's", await File.ReadAllTextAsync(taken, Ct));
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(_folder, "report (1).txt"), Ct));
        Assert.Equal(["report (1).txt", "report.txt"], Names());
    }

    [Fact]
    public async Task A_file_name_near_the_limit_can_still_be_uploaded()
    {
        var name = new string('n', 250) + ".bin";
        var content = Bytes(1500);

        var result = await UploadAsync(Request(name, (ulong)content.Length, ConflictPolicy.Overwrite), content);

        // The partial's name is the file's plus about forty characters; it has to be shortened to a name that exists.
        Assert.Equal(FileError.None, result.Complete!.Complete.Status.Error);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(_folder, name), Ct));
        Assert.Equal([name], Names());
    }

    [Fact]
    public async Task A_replacement_that_cannot_be_put_in_place_leaves_the_original_untouched_and_is_reported()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Needs a file that can be seen but not replaced, which a held lock gives on Windows.");

        var path = Path.Combine(_folder, "locked.txt");
        await File.WriteAllTextAsync(path, "original", Ct);
        var content = Bytes(4000);

        FileResponse reply;
        await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            reply = (await UploadAsync(Request("locked.txt", (ulong)content.Length, ConflictPolicy.Overwrite), content)).Complete!;
        }

        // The controller is told, instead of just seeing the channel close.
        Assert.NotEqual(FileError.None, reply.Complete.Status.Error);
        Assert.Equal("original", await File.ReadAllTextAsync(path, Ct));

        // No temporary file is left under a name nobody would recognise; the finished partial stays for a retry.
        Assert.DoesNotContain(Names(), name => name.StartsWith(".rd-", StringComparison.Ordinal));
        Assert.Contains(Names(), name => name.EndsWith(PartialFile.Extension, StringComparison.Ordinal));
    }

    private UploadRequest Request(string name, ulong size, ConflictPolicy policy) => new()
    {
        Directory = _folder,
        FileName = name,
        Size = size,
        ModifiedUnixMs = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
        ConflictPolicy = policy,
    };

    private string[] Names() =>
        [.. Directory.GetFileSystemEntries(_folder).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    private static byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// Sends one upload request, the content, and the finish message, and returns what the host answered at each
    /// step. <paramref name="afterAccepted"/> runs between the host's acceptance and the first byte of content.
    /// </summary>
    private static async Task<(FileResponse First, FileResponse? Complete)> UploadAsync(
        UploadRequest request,
        byte[] content,
        Action<UploadAccepted>? afterAccepted = null)
    {
        var (moduleEnd, controller) = await PipePair.CreateAsync();
        var module = new FileTransferModule(new AllowEverything(), new FileAccessPolicy(), NullLogger<FileTransferModule>.Instance);
        var run = module.HandleAsync(new TestChannel(moduleEnd), TestSession, Ct);
        try
        {
            await ProtobufFrames.WriteAsync(controller, new FileRequest { Upload = request }, Ct);
            var first = await ProtobufFrames.ReadAsync(controller, FileResponse.Parser, cancellationToken: Ct)
                ?? throw new InvalidDataException("The host closed the channel.");
            if (first.BodyCase != FileResponse.BodyOneofCase.UploadAccepted || first.UploadAccepted.Status.Error != FileError.None)
            {
                return (first, null);
            }

            afterAccepted?.Invoke(first.UploadAccepted);
            if (content.Length > 0)
            {
                await FileChunkFrame.WriteAsync(controller, content, Ct);
            }

            await ProtobufFrames.WriteAsync(
                controller,
                new FileRequest { Finish = new TransferComplete { Status = new FileStatus(), Hash = XxHash64.HashToUInt64(content), Size = (ulong)content.Length } },
                Ct);
            var complete = await ProtobufFrames.ReadAsync(controller, FileResponse.Parser, cancellationToken: Ct);
            return (first, complete);
        }
        finally
        {
            await controller.DisposeAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
    }

    private sealed class AllowEverything : IUserContextGuard
    {
        public string? GetRefusalReason() => null;
    }

    private sealed class TestChannel(Stream stream) : IChannel
    {
        public ChannelType Type => ChannelType.FileTransfer;

        public Stream Stream => stream;

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }

    private static class PipePair
    {
        public static async Task<(Stream Module, Stream Controller)> CreateAsync()
        {
            var name = $"rd-unit-{Guid.NewGuid():N}";
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync());
            return (server, client);
        }
    }
}
