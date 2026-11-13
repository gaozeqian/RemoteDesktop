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
/// The sizes and offsets in a host's answers are the host's to announce. Cast to a signed length without a check, a
/// value above long.MaxValue turns negative and the client would start a transfer from nonsense.
/// </summary>
public sealed class FileClientAnnouncedSizeTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("rd-announced-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task A_download_whose_announced_size_does_not_fit_a_signed_length_is_refused()
    {
        var observer = new RecordingObserver();
        var answer = new FileResponse { DownloadAccepted = new DownloadAccepted { Status = new FileStatus(), Size = ulong.MaxValue } };

        var failure = await Assert.ThrowsAsync<FileOperationException>(() => DownloadAsync(answer, observer));

        Assert.Equal(FileError.Unsupported, failure.Error);
        Assert.False(observer.Started);
        Assert.Empty(Directory.GetFileSystemEntries(_folder));
    }

    [Theory]
    [InlineData(ulong.MaxValue)]
    [InlineData((ulong)long.MaxValue + 1)]
    [InlineData(11UL)] // beyond the 10-byte file
    public async Task An_upload_resume_offset_beyond_the_file_is_refused_before_anything_is_sent(ulong resumeOffset)
    {
        var observer = new RecordingObserver();
        var answer = new FileResponse { UploadAccepted = new UploadAccepted { Status = new FileStatus(), FinalPath = "/remote/up.bin", ResumeOffset = resumeOffset } };

        var failure = await Assert.ThrowsAsync<FileOperationException>(() => UploadAsync(answer, observer));

        Assert.Equal(FileError.Unsupported, failure.Error);
        Assert.False(observer.Started);
    }

    private async Task<TransferOutcome> DownloadAsync(FileResponse answer, ITransferObserver observer)
    {
        var (hostEnd, clientEnd) = await PipePair.CreateAsync();
        var host = HostAsync(hostEnd, answer);
        try
        {
            return await FileTransferClient.DownloadAsync(new StubConnection(clientEnd), "/remote/file.bin", _folder, "file.bin", ConflictPolicy.Overwrite, observer, Ct);
        }
        finally
        {
            await clientEnd.DisposeAsync();
            await host;
        }
    }

    private async Task<TransferOutcome> UploadAsync(FileResponse answer, ITransferObserver observer)
    {
        var source = Path.Combine(_folder, "source.bin");
        await File.WriteAllBytesAsync(source, new byte[10], Ct);

        var (hostEnd, clientEnd) = await PipePair.CreateAsync();
        var host = HostAsync(hostEnd, answer);
        try
        {
            return await FileTransferClient.UploadAsync(new StubConnection(clientEnd), source, "/remote", "up.bin", ConflictPolicy.Overwrite, observer, Ct);
        }
        finally
        {
            await clientEnd.DisposeAsync();
            await host;
        }
    }

    /// <summary>A scripted host: reads the request, gives the canned answer, and says nothing more.</summary>
    private static async Task HostAsync(Stream wire, FileResponse answer)
    {
        await using (wire)
        {
            try
            {
                _ = await ProtobufFrames.ReadAsync(wire, FileRequest.Parser, cancellationToken: Ct);
                await ProtobufFrames.WriteAsync(wire, answer, Ct);
                _ = await ProtobufFrames.ReadAsync(wire, FileRequest.Parser, cancellationToken: Ct); // until the client leaves
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidDataException)
            {
                // The client gave up and closed the channel.
            }
        }
    }

    private sealed class RecordingObserver : ITransferObserver
    {
        public bool Started { get; private set; }

        void ITransferObserver.Started(long totalBytes, long startOffset, string finalPath, string partialPath) => Started = true;

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
        public static async Task<(Stream Host, Stream Client)> CreateAsync()
        {
            var name = $"rd-unit-{Guid.NewGuid():N}";
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync());
            return (server, client);
        }
    }
}
