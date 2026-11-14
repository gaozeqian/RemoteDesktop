using System.Threading.Channels;
using RemoteDesktop.Abstractions.Media;

namespace RemoteDesktop.Platform.Shared;

/// <summary>
/// The bounded frame queue every recorder writes behind. One second of frames; older ones are dropped when the disk
/// or the encoder cannot keep up, because a recording that skips is better than a queue that grows without bound.
/// </summary>
/// <remarks>
/// <para>
/// The three recorders had this loop three times, and had already drifted: Linux logged a write failure and kept
/// going, while Windows and macOS stopped. <see cref="VideoRecorderFailure"/> says why <em>nothing more</em> will be
/// written, so stopping is the contract; the first reason recorded is the one kept.
/// </para>
/// <para>
/// The writer callback returns a failure instead of throwing when the platform's native call reports an error; a
/// thrown exception is treated as <see cref="VideoRecorderFailure.WriteFailed"/> as well. Either way the worker stops:
/// the recording cannot go on after a write it could not complete.
/// </para>
/// </remarks>
internal sealed class RecorderFrameQueue<T>
{
    /// <summary>A second of frames at the usual rate. Beyond that the recording is behind live and dropping is kind.</summary>
    public const int Capacity = 30;

    private readonly Channel<T> _frames;
    private readonly Task _worker;
    private long _written;
    private int _failure;

    /// <param name="write">Writes one frame and returns why the recording must stop, or <c>null</c> on success.</param>
    public RecorderFrameQueue(Func<T, VideoRecorderFailure?> write)
    {
        ArgumentNullException.ThrowIfNull(write);

        _frames = Channel.CreateBounded<T>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _worker = Task.Run(() => RunAsync(write));
    }

    public long WrittenFrames => Interlocked.Read(ref _written);

    public VideoRecorderFailure Failure => (VideoRecorderFailure)Volatile.Read(ref _failure);

    /// <summary>Records the first reason; later reasons do not replace it.</summary>
    public void Fail(VideoRecorderFailure failure) =>
        Interlocked.CompareExchange(ref _failure, (int)failure, (int)VideoRecorderFailure.None);

    /// <summary>Queues a frame; false when the recording has already been stopped.</summary>
    public bool TryEnqueue(T frame) => _frames.Writer.TryWrite(frame);

    /// <summary>No more frames will be queued; the worker finishes what is already there.</summary>
    public void Complete() => _frames.Writer.TryComplete();

    /// <summary>Completes when the queue has been completed and the worker has drained it (or stopped on a failure).</summary>
    public Task DrainAsync() => _worker;

    private async Task RunAsync(Func<T, VideoRecorderFailure?> write)
    {
        await foreach (var frame in _frames.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            VideoRecorderFailure? failure;
            try
            {
                failure = write(frame);
            }
#pragma warning disable CA1031 // A failed recording must not end the session that is being recorded.
            catch (Exception)
#pragma warning restore CA1031
            {
                failure = VideoRecorderFailure.WriteFailed;
            }

            if (failure is { } reason)
            {
                Fail(reason);
                return;
            }

            Interlocked.Increment(ref _written);
        }
    }
}
