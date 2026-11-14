using System.Threading.Channels;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.MacOS.Interop;
using RemoteDesktop.Platform.Shared;

namespace RemoteDesktop.Platform.MacOS.Camera;

/// <summary>
/// Receives camera pictures from the native helper's queue and hands them to <see cref="MacCameraSource"/>.
/// </summary>
/// <remarks>
/// <para>
/// The helper calls <see cref="Deliver"/> on a serial dispatch queue, so deliveries never overlap with each other.
/// The consumer reads from another thread, which is what the channel is for.
/// </para>
/// <para>
/// Only one frame is held. If the consumer is slower than the camera, the newer picture replaces the older one
/// rather than queueing behind it — a live view wants the most recent picture, not a backlog — and the replaced
/// frame goes back to the pool immediately. This is the same shape the Windows source reader uses, and it is why
/// the native side sets <c>alwaysDiscardsLateVideoFrames</c>: both ends drop rather than accumulate.
/// </para>
/// <para>
/// The copy in <see cref="Deliver"/> is required, not an optimisation that was skipped. AVFoundation lends the
/// pixel buffer for the duration of the callback only, so anything the consumer might read later has to be moved
/// into memory this side owns before returning.
/// </para>
/// </remarks>
internal sealed class MacCameraFrameSink : IDisposable
{
    private readonly Channel<MemoryVideoFrame> _frames = Channel.CreateBounded<MemoryVideoFrame>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        },
        dropped => dropped.Dispose());

    private bool _disposed;

    public ChannelReader<MemoryVideoFrame> Reader => _frames.Reader;

    /// <summary>Called from the helper's queue. Must not throw: it is reached through a reverse P/Invoke.</summary>
    public unsafe void Deliver(RdCameraFrame* frame)
    {
        if (frame is null || frame->Bgra is null || frame->Width < 2 || frame->Height < 2)
        {
            return;
        }

        var width = frame->Width;
        var height = frame->Height;

        MemoryVideoFrame delivered;
        try
        {
            delivered = new MemoryVideoFrame(width, height, PixelFormat.Bgra32, HostTime.ToTimestamp(frame->HostTimeNanoseconds));
        }
        catch (ArgumentException)
        {
            // Impossible dimensions are dropped rather than allowed to escape into Objective-C, where unwinding
            // would be undefined.
            return;
        }

        var destination = delivered.Buffer;
        var sourceRow = frame->Bgra;
        var sourceStride = frame->BytesPerRow;
        var destinationStride = delivered.Stride;

        // The source rows are padded to a hardware alignment that is usually wider than the visible width, so this
        // is a row-by-row copy rather than one memcpy over the whole buffer.
        BgraRowCopy.Copy(sourceRow, sourceStride, destination, destinationStride, height);

        // A write into a completed channel is a no-op that reports false, which is what happens for a picture that
        // was in flight while the capture was being torn down. The frame has to be released by hand in that case.
        if (!_frames.Writer.TryWrite(delivered))
        {
            delivered.Dispose();
        }
    }

    /// <summary>Ends the stream. A pending failure is handed to the consumer through the channel.</summary>
    public void Complete(Exception? error = null) => _frames.Writer.TryComplete(error);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Complete();

        // Anything still queued is released here; the channel does not own its items.
        while (_frames.Reader.TryRead(out var left))
        {
            left.Dispose();
        }
    }

}
