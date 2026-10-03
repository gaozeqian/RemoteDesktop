using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace RemoteDesktop.Abstractions.Media;

/// <summary>
/// A CPU frame in pooled memory. NV12 is stored as a Y plane (<see cref="Stride"/> × height) followed by the
/// interleaved UV plane (<see cref="Stride"/> × height / 2); width and height must be even.
/// </summary>
public sealed class MemoryVideoFrame : VideoFrame
{
    private byte[]? _buffer;

    [SetsRequiredMembers]
    public MemoryVideoFrame(int width, int height, PixelFormat format, TimeSpan timestamp)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 2);
        if (format == PixelFormat.Nv12 && (width % 2 != 0 || height % 2 != 0))
        {
            throw new ArgumentException("NV12 frames need even dimensions.", nameof(width));
        }

        Width = width;
        Height = height;
        Format = format;
        Timestamp = timestamp;
        Stride = format == PixelFormat.Bgra32 ? width * 4 : width;
        Length = GetLength(format, Stride, height);
        _buffer = ArrayPool<byte>.Shared.Rent(Length);
    }

    public int Stride { get; }

    public int Length { get; }

    /// <summary>Writable pixel memory, valid until the frame is disposed.</summary>
    public Span<byte> Buffer => (_buffer ?? throw new ObjectDisposedException(nameof(MemoryVideoFrame))).AsSpan(0, Length);

    public override ReadOnlySpan<byte> GetPixels(out int stride)
    {
        stride = Stride;
        return Buffer;
    }

    public static int GetLength(PixelFormat format, int stride, int height) => format switch
    {
        PixelFormat.Bgra32 => stride * height,
        PixelFormat.Nv12 or PixelFormat.I420 => stride * height * 3 / 2,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    protected override void Dispose(bool disposing)
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        base.Dispose(disposing);
    }
}
