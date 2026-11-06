using RemoteDesktop.Abstractions.Media;

namespace RemoteDesktop.Media;

/// <summary>
/// The double-buffered BGRA picture shared by the desktop and camera viewers: the decode loop converts into the back
/// buffer outside the lock and swaps, while the UI copies the front buffer under it.
/// </summary>
/// <remarks>
/// Both viewers had this code inline and it had already drifted once. Keeping it here means one place decides when a
/// copy is refused (no picture yet, or a size the UI did not ask for).
/// </remarks>
public sealed class VideoFrameBuffer
{
    private readonly Lock _gate = new();
    private byte[] _front = [];
    private byte[] _back = [];
    private long _frameNumber;
    private int _width;
    private int _height;

    /// <summary>Total number of pictures published since the last <see cref="Reset"/>.</summary>
    public long FrameNumber
    {
        get
        {
            lock (_gate)
            {
                return _frameNumber;
            }
        }
    }

    /// <summary>Starts a new stream at the given size. The next publish reallocates if the size differs.</summary>
    public void Reset(int width, int height)
    {
        lock (_gate)
        {
            _width = width;
            _height = height;
            if (_front.Length != width * height * 4)
            {
                _front = new byte[width * height * 4];
                _back = new byte[width * height * 4];
            }

            _frameNumber = 0;
        }
    }

    /// <summary>Converts one decoded NV12 picture into BGRA and makes it the current frame.</summary>
    /// <exception cref="ArgumentException">The picture does not match the size last passed to <see cref="Reset"/>.</exception>
    public void Publish(VideoFrame picture)
    {
        ArgumentNullException.ThrowIfNull(picture);

        // Convert outside the lock: a 4K colour conversion must not serialize the UI's copy against the decoder.
        var width = picture.Width;
        var height = picture.Height;
        ColorConversion.Nv12ToBgra(picture.GetPixels(out var stride), stride, width, height, BackBuffer(width, height), width * 4);

        lock (_gate)
        {
            (_front, _back) = (_back, _front);
            _frameNumber++;
        }
    }

    /// <summary>
    /// Copies the newest picture into <paramref name="destination"/> (BGRA, <paramref name="stride"/> bytes per row).
    /// Returns its sequence number, or <c>-1</c> when there is no picture of that size yet.
    /// </summary>
    public long CopyLatest(Span<byte> destination, int stride, int width, int height)
    {
        lock (_gate)
        {
            if (_frameNumber == 0 || _width != width || _height != height)
            {
                return -1;
            }

            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
            {
                _front.AsSpan(y * rowBytes, rowBytes).CopyTo(destination.Slice(y * stride, rowBytes));
            }

            return _frameNumber;
        }
    }

    /// <summary>The back buffer, reallocated if a stream announced a size that <see cref="Reset"/> has not seen yet.</summary>
    private byte[] BackBuffer(int width, int height)
    {
        lock (_gate)
        {
            if (_back.Length != width * height * 4)
            {
                _front = new byte[width * height * 4];
                _back = new byte[width * height * 4];
                _width = width;
                _height = height;
                _frameNumber = 0;
            }

            return _back;
        }
    }
}
