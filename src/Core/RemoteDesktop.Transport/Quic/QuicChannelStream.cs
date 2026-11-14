using System.Net.Quic;
using System.Runtime.Versioning;

namespace RemoteDesktop.Transport.Quic;

/// <summary>
/// The <see cref="Stream"/> a feature module sees for one QUIC stream.
/// </summary>
/// <remarks>
/// Feature loops are written against the ordinary stream contract: a peer that goes away surfaces as
/// <see cref="IOException"/> (or the stream ending), which their catch filters already handle. A <c>QuicStream</c>
/// reports an aborted connection as <see cref="QuicException"/> instead, so that exception used to escape those
/// filters, fault the loop and break the disposal chain. Translating it here keeps the transport detail inside the
/// transport assembly: callers keep treating <see cref="IOException"/> as "the peer is gone".
/// </remarks>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed class QuicChannelStream(QuicStream inner) : Stream
{
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        try
        {
            inner.Flush();
        }
        catch (QuicException ex)
        {
            throw PeerGone(ex);
        }
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (QuicException ex)
        {
            throw PeerGone(ex);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            return inner.Read(buffer, offset, count);
        }
        catch (QuicException ex)
        {
            throw PeerGone(ex);
        }
    }

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return inner.Read(buffer);
        }
        catch (QuicException ex)
        {
            throw PeerGone(ex);
        }
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        try
        {
            return await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }
        catch (QuicException ex)
        {
            throw PeerGone(ex);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (QuicException ex)
        {
            throw PeerGone(ex);
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        try
        {
            inner.Write(buffer, offset, count);
        }
        catch (QuicException ex)
        {
            throw PeerGone(ex);
        }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        try
        {
            inner.Write(buffer);
        }
        catch (QuicException ex)
        {
            throw PeerGone(ex);
        }
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        try
        {
            await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }
        catch (QuicException ex)
        {
            throw PeerGone(ex);
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (QuicException ex)
        {
            throw PeerGone(ex);
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        catch (QuicException ex)
        {
            throw PeerGone(ex);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                inner.Dispose();
            }
            catch (QuicException ex)
            {
                throw PeerGone(ex);
            }
        }

        base.Dispose(disposing);
    }

    private static IOException PeerGone(QuicException exception) =>
        new("The peer's QUIC stream was aborted.", exception);
}
