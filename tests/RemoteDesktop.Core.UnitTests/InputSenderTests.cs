using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Desktop;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Core.UnitTests;

public sealed class InputSenderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_full_queue_applies_backpressure_without_losing_key_releases()
    {
        var stream = new BlockingWriteStream(expectedFrames: 3);
        var connection = new FakeConnection(new TestChannel(stream));
        var sender = await InputSender.OpenAsync(connection, Ct, queueCapacity: 1);

        sender.Send(new KeyEvent(0x1D, false, true));
        await stream.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        sender.Send(new KeyEvent(0x1D, false, false));
        var blocked = Task.Run(() => sender.Send(new KeyEvent(0x2A, false, true)), Ct);
        await Task.Delay(50, Ct);
        Assert.False(blocked.IsCompleted, "A reliable event should wait rather than disappear when the queue is full.");

        stream.ReleaseWrites();
        await blocked.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await stream.AllWrites.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await sender.DisposeAsync();

        stream.Bytes.Position = 0;
        var keys = new List<Key>();
        while (await ProtobufFrames.ReadAsync(stream.Bytes, InputMessage.Parser, 4096, Ct) is { } message)
        {
            keys.Add(message.Key);
        }

        Assert.Equal(3, keys.Count);
        Assert.Equal([true, false, true], keys.Select(key => key.Down).ToArray());
    }

    [Fact]
    public async Task Congestion_replaces_an_older_pointer_move_with_the_latest_position()
    {
        var stream = new BlockingWriteStream(expectedFrames: 2);
        var sender = await InputSender.OpenAsync(new FakeConnection(new TestChannel(stream)), Ct, queueCapacity: 1);
        sender.Send(new PointerMoveEvent("display", 0.1, 0.1));
        await stream.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        sender.Send(new PointerMoveEvent("display", 0.2, 0.2));
        sender.Send(new PointerMoveEvent("display", 0.3, 0.3));

        stream.ReleaseWrites();
        await stream.AllWrites.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await sender.DisposeAsync();

        stream.Bytes.Position = 0;
        var moves = new List<PointerMove>();
        while (await ProtobufFrames.ReadAsync(stream.Bytes, InputMessage.Parser, 4096, Ct) is { } message)
        {
            moves.Add(message.PointerMove);
        }

        Assert.Equal(2, moves.Count);
        Assert.Equal([0.1, 0.3], moves.Select(move => move.X).ToArray());
    }

    private sealed class FakeConnection(IChannel channel) : IPeerConnection
    {
        public DeviceId RemoteDevice => new(123456789);

        public ConnectionPath Path => ConnectionPath.Direct;

        public string? RelaySessionId => null;

        public string LocalFingerprint => "local";

        public string RemoteFingerprint => "remote";

        public ValueTask<IChannel> OpenChannelAsync(ChannelType type, bool bidirectional, CancellationToken cancellationToken) =>
            ValueTask.FromResult(channel);

        public ValueTask<IChannel?> AcceptChannelAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IChannel?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestChannel(Stream stream) : IChannel
    {
        public ChannelType Type => ChannelType.Input;

        public Stream Stream => stream;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingWriteStream(int expectedFrames) : Stream
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;

        public TaskCompletionSource FirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllWrites { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MemoryStream Bytes { get; } = new();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            FirstWrite.TrySetResult();
            var write = Interlocked.Increment(ref _writes);
            if (write == 1)
            {
                await _release.Task.WaitAsync(cancellationToken);
            }

            await Bytes.WriteAsync(buffer, cancellationToken);

            // FrameCodec writes every frame in two calls: a header and then the payload. Counting calls used to
            // announce "all writes" in the middle of the last frame, so a test that disposed the sender as soon as
            // that happened cancelled the frame being written and read back one message fewer. Only a finished
            // payload means a finished frame; all the messages in these tests have a payload.
            if (write % 2 == 0 && write / 2 >= expectedFrames)
            {
                AllWrites.TrySetResult();
            }
        }

        public void ReleaseWrites() => _release.TrySetResult();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => Bytes.Length;

        public override long Position { get; set; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Bytes.Write(buffer, offset, count);
    }
}
