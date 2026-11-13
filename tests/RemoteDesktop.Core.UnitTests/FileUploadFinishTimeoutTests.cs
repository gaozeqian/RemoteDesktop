using System.Diagnostics;
using System.IO.Pipes;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.File;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Transport;
using RemoteDesktop.TestSupport;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// After the last byte of an upload the host copies the whole partial file to its final name before it answers, so
/// how long that takes grows with the file. The wait for that answer therefore has to grow with it; the other
/// requests of a transfer are answered at once and keep the plain deadline.
/// </summary>
public sealed class FileUploadFinishTimeoutTests : IDisposable
{
    private const int Size = 1024 * 1024;

    private readonly string _folder = Directory.CreateTempSubdirectory("rd-finish-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task A_host_that_is_slow_to_put_the_file_in_place_is_waited_for_longer_than_the_plain_deadline()
    {
        var baseTimeout = TimeSpan.FromMilliseconds(300);

        // 800 ms is far past 300 ms but inside the 300 ms + 1 s the client allows for a 1 MiB file.
        var outcome = await UploadAsync(baseTimeout, finishDelay: TimeSpan.FromMilliseconds(800));

        Assert.Equal(TransferOutcome.Completed, outcome);
    }

    [Fact]
    public async Task A_host_that_never_answers_still_ends_the_upload_with_an_io_error_after_the_longer_deadline()
    {
        var baseTimeout = TimeSpan.FromMilliseconds(300);
        var clock = Stopwatch.StartNew();

        var failure = await Assert.ThrowsAsync<FileOperationException>(() => UploadAsync(baseTimeout, finishDelay: Timeout.InfiniteTimeSpan));

        Assert.Equal(FileError.Io, failure.Error);
        Assert.True(clock.Elapsed > TimeSpan.FromSeconds(1), $"Gave up after only {clock.Elapsed}.");
    }

    [Fact]
    public void The_wait_grows_by_one_second_per_mebibyte_up_to_an_hour()
    {
        var plain = TimeSpan.FromSeconds(30);

        Assert.Equal(plain, FileTransferClient.FinishTimeout(plain, 0));
        Assert.Equal(TimeSpan.FromSeconds(130), FileTransferClient.FinishTimeout(plain, 100L * 1024 * 1024));
        Assert.Equal(TimeSpan.FromHours(1), FileTransferClient.FinishTimeout(plain, 100L * 1024 * 1024 * 1024));
        Assert.Equal(TimeSpan.FromHours(2), FileTransferClient.FinishTimeout(TimeSpan.FromHours(2), 1024)); // never shorter than asked for
    }

    private async Task<TransferOutcome> UploadAsync(TimeSpan requestTimeout, TimeSpan finishDelay)
    {
        var source = Path.Combine(_folder, "source.bin");
        var content = new byte[Size];
        new Random(7).NextBytes(content);
        await File.WriteAllBytesAsync(source, content, Ct);

        var (hostEnd, controllerEnd) = await PipePair.CreateAsync();
        using var done = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var host = HostAsync(hostEnd, finishDelay, done.Token);
        try
        {
            return await FileTransferClient.UploadAsync(
                new StubConnection(controllerEnd),
                source,
                _folder,
                "up.bin",
                ConflictPolicy.Overwrite,
                new NullObserver(),
                Ct,
                requestTimeout);
        }
        finally
        {
            await done.CancelAsync();
            await controllerEnd.DisposeAsync();
            await host;
        }
    }

    /// <summary>The host side: accepts the upload, takes the content, and answers the finish after <paramref name="finishDelay"/>.</summary>
    private async Task HostAsync(Stream wire, TimeSpan finishDelay, CancellationToken cancellationToken)
    {
        await using (wire)
        {
            try
            {
                var request = await ProtobufFrames.ReadAsync(wire, FileRequest.Parser, cancellationToken: cancellationToken)
                    ?? throw new InvalidDataException("The client closed the channel.");
                await ProtobufFrames.WriteAsync(
                    wire,
                    new FileResponse { UploadAccepted = new UploadAccepted { Status = new FileStatus(), FinalPath = Path.Combine(_folder, "up.bin") } },
                    cancellationToken);

                var size = (long)request.Upload.Size;
                var hash = await FileReceiver.ReceiveAsync(wire, Path.Combine(_folder, "host.rdpart"), size, 0, null, cancellationToken);
                _ = await ProtobufFrames.ReadAsync(wire, FileRequest.Parser, 64 * 1024, cancellationToken);

                // The copy into the final file, which is what takes the time on a large file or a slow disk.
                await Task.Delay(finishDelay, cancellationToken);
                await ProtobufFrames.WriteAsync(
                    wire,
                    new FileResponse { Complete = new TransferComplete { Status = new FileStatus(), Hash = hash, Size = (ulong)size } },
                    cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // The client gave up and closed the channel, or the test is over.
            }
        }
    }

    private sealed class NullObserver : ITransferObserver
    {
        public void Started(long totalBytes, long startOffset, string finalPath, string partialPath)
        {
        }

        public void Progress(long bytes)
        {
        }
    }

    private sealed class StubConnection(Stream clientEnd) : IPeerConnection
    {
        public DeviceId RemoteDevice => new(123456789);

        public ConnectionPath Path => ConnectionPath.Direct;

        public string? RelaySessionId => null;

        public string LocalFingerprint => "local";

        public string RemoteFingerprint => "remote";

        public ValueTask<IChannel> OpenChannelAsync(ChannelType type, bool bidirectional, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IChannel>(new TestChannel(ChannelType.FileTransfer, clientEnd));

        public ValueTask<IChannel?> AcceptChannelAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IChannel?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static class PipePair
    {
        public static async Task<(Stream Host, Stream Controller)> CreateAsync()
        {
            var name = $"rd-unit-{Guid.NewGuid():N}";
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync());
            return (server, client);
        }
    }
}
