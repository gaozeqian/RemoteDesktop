using RemoteDesktop.Protocol.Control;
using RemoteDesktop.Protocol.Framing;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The read side already checks cancellation; the write side has two separate writes (header, then payload), so a
/// token that is ignored would leave a half frame on a live channel and the peer would parse the next frame wrong.
/// </summary>
public class FrameCodecWriteCancellationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_already_cancelled_token_is_observed_before_any_byte_is_written()
    {
        using var stream = new CancelObservingStream();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await FrameCodec.WriteAsync(stream, new Frame(7, new byte[] { 1, 2, 3 }), cancelled.Token));

        Assert.Equal(0, stream.BytesWritten);
    }

    [Fact]
    public async Task Cancellation_between_header_and_payload_leaves_only_the_header()
    {
        using var stream = new CancelObservingStream();
        using var cancel = new CancellationTokenSource();
        stream.CancelAfterFirstWrite = cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await FrameCodec.WriteAsync(stream, new Frame(7, new byte[16]), cancel.Token));

        Assert.Equal(FrameCodec.HeaderSize, stream.BytesWritten);
    }

    [Fact]
    public async Task Envelope_writing_passes_the_token_to_the_same_writer()
    {
        using var stream = new CancelObservingStream();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ProtobufFrames.WriteAsync(stream, new ControlMessage { Ping = new Ping { Timestamp = 1 } }, cancelled.Token));

        Assert.Equal(0, stream.BytesWritten);
    }

    /// <summary>
    /// Observes the token the writer was given. The product passes the token straight to Stream.WriteAsync; a stream
    /// that ignores it would let a cancelled write succeed, which is exactly the regression these cases detect.
    /// </summary>
    private sealed class CancelObservingStream : Stream
    {
        public int BytesWritten { get; private set; }

        public CancellationTokenSource? CancelAfterFirstWrite { get; set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BytesWritten += buffer.Length;
            CancelAfterFirstWrite?.Cancel();
            return ValueTask.CompletedTask;
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => BytesWritten;

        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
