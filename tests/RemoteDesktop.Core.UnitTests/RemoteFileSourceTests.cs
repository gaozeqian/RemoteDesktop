using RemoteDesktop.Domain;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.File;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The file pane asks the other computer one question at a time and waits for the answer. A host that accepted the
/// channel and then said nothing left it waiting for as long as the session lasted.
/// </summary>
public sealed class RemoteFileSourceTests
{
    [Fact]
    public async Task A_host_that_never_answers_ends_the_request_instead_of_hanging()
    {
        var source = new RemoteFileSource(new SilentConnection(), TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<FileOperationException>(() => source.ListAsync("/home", CancellationToken.None));

        Assert.Equal(FileError.Io, ex.Error);
        Assert.Contains("did not answer", ex.Message, StringComparison.Ordinal);
    }

    private sealed class SilentConnection : IPeerConnection
    {
        public DeviceId RemoteDevice => default;

        public ConnectionPath Path => ConnectionPath.Direct;

        public string? RelaySessionId => null;

        public string LocalFingerprint => "local";

        public string RemoteFingerprint => "remote";

        public ValueTask<IChannel> OpenChannelAsync(ChannelType type, bool bidirectional, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IChannel>(new SilentChannel(type));

        public ValueTask<IChannel?> AcceptChannelAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IChannel?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SilentChannel(ChannelType type) : IChannel
    {
        public ChannelType Type => type;

        public Stream Stream { get; } = new SilentStream();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Takes whatever is written and never answers a read: a host that accepted the channel and went quiet.</summary>
    private sealed class SilentStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
