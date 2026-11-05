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

    [Fact]
    public async Task Disposing_the_source_while_a_request_is_in_flight_does_not_turn_its_failure_into_an_object_disposed_error()
    {
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new RemoteFileSource(new GatedConnection(gate), TimeSpan.FromSeconds(30));

        var pending = source.ListAsync("/home", TestContext.Current.CancellationToken);
        await source.DisposeAsync();
        gate.SetException(new IOException("The channel was closed."));

        // The request still ends the way a lost connection always does, rather than failing on the released lock.
        var ex = await Assert.ThrowsAsync<FileOperationException>(() => pending);
        Assert.Equal(FileError.Io, ex.Error);
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

    private sealed class GatedConnection(TaskCompletionSource<int> gate) : IPeerConnection
    {
        public DeviceId RemoteDevice => default;

        public ConnectionPath Path => ConnectionPath.Direct;

        public string? RelaySessionId => null;

        public string LocalFingerprint => "local";

        public string RemoteFingerprint => "remote";

        public ValueTask<IChannel> OpenChannelAsync(ChannelType type, bool bidirectional, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IChannel>(new GatedChannel(type, new GatedStream(gate)));

        public ValueTask<IChannel?> AcceptChannelAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IChannel?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class GatedChannel(ChannelType type, Stream stream) : IChannel
    {
        public ChannelType Type => type;

        public Stream Stream => stream;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Takes whatever is written; a read ends only when the test completes the gate.</summary>
    private sealed class GatedStream(TaskCompletionSource<int> gate) : Stream
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

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            await gate.Task.WaitAsync(cancellationToken);
    }
}
