using System.Collections.Concurrent;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Shared;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The queue the three recorders write behind. Its contract is what a recording promises when the disk cannot keep
/// up: write in order, drop the oldest instead of growing, and stop at the first reason the file cannot continue.
/// </summary>
public sealed class RecorderFrameQueueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Frames_are_written_in_order_and_counted()
    {
        var written = new ConcurrentQueue<int>();
        var queue = new RecorderFrameQueue<int>(frame =>
        {
            written.Enqueue(frame);
            return null;
        });

        for (var i = 0; i < 10; i++)
        {
            Assert.True(queue.TryEnqueue(i));
        }

        queue.Complete();
        await queue.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(Enumerable.Range(0, 10), written);
        Assert.Equal(10, queue.WrittenFrames);
        Assert.Equal(VideoRecorderFailure.None, queue.Failure);
    }

    [Fact]
    public async Task A_write_failure_stops_the_worker_and_keeps_the_first_reason()
    {
        var written = new ConcurrentQueue<int>();
        var queue = new RecorderFrameQueue<int>(frame =>
        {
            if (frame == 2)
            {
                return VideoRecorderFailure.WriteFailed;
            }

            written.Enqueue(frame);
            return null;
        });

        for (var i = 0; i < 5; i++)
        {
            queue.TryEnqueue(i);
        }

        queue.Complete();
        await queue.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // The first two frames are in the file; the failed one and everything after it are not.
        Assert.Equal([0, 1], written);
        Assert.Equal(2, queue.WrittenFrames);
        Assert.Equal(VideoRecorderFailure.WriteFailed, queue.Failure);
    }

    [Fact]
    public async Task A_throwing_writer_is_a_write_failure_rather_than_a_faulted_task()
    {
        var queue = new RecorderFrameQueue<int>(_ => throw new IOException("disk full"));

        queue.TryEnqueue(1);
        queue.Complete();
        await queue.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(VideoRecorderFailure.WriteFailed, queue.Failure);
        Assert.Equal(0, queue.WrittenFrames);
    }

    [Fact]
    public async Task A_full_queue_drops_the_oldest_frames_rather_than_growing()
    {
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var written = new ConcurrentQueue<int>();
        var queue = new RecorderFrameQueue<int>(frame =>
        {
            if (frame == 0)
            {
                // Hold the worker on the first frame so the queue behind it fills and has to drop.
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5), Ct));
            }

            written.Enqueue(frame);
            return null;
        });

        Assert.True(queue.TryEnqueue(0));

        // Wait until frame 0 is in the worker's hands, not merely queued: otherwise it occupies one of the 30 slots
        // and the count below depends on how quickly the worker was scheduled.
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), Ct));
        for (var i = 1; i <= 100; i++)
        {
            queue.TryEnqueue(i);
        }

        release.Set();
        queue.Complete();
        await queue.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // One frame is in the worker's hands; the queue holds 30. The newest 30 of the 100 survive, and the oldest
        // are gone, which is the "skip rather than run out of memory" promise.
        Assert.Equal(RecorderFrameQueue<int>.Capacity, written.Count - 1);
        Assert.Equal(0, written.First());
        Assert.DoesNotContain(written, frame => frame is > 0 and < 71);
        Assert.Equal(100, written.Last());
    }

    [Fact]
    public void A_completed_queue_refuses_new_frames()
    {
        var queue = new RecorderFrameQueue<int>(_ => null);
        queue.Complete();

        Assert.False(queue.TryEnqueue(1));
    }
}
