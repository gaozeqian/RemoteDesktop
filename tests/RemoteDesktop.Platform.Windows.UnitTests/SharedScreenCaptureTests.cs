using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Windows.Display;

namespace RemoteDesktop.Platform.Windows.UnitTests;

/// <summary>
/// Windows allows one Desktop Duplication interface per output per process, so concurrent sessions subscribe to one
/// capture instead of asking DXGI for a second. The second session used to end at its first frame with E_INVALIDARG.
/// </summary>
/// <remarks>
/// The fake capturer stands in for DXGI: it records how many were created and hands frames over on demand, which is
/// all the sharing layer needs. It runs on every platform because nothing here reaches Windows.
/// </remarks>
public class SharedScreenCaptureTests
{
    private static readonly DisplayInfo TestDisplay = new("display-1", "Test", new PixelRect(0, 0, 4, 4), 1, IsPrimary: true);

    [Fact]
    public async Task Two_subscribers_share_one_capturer_and_both_receive_the_same_frame()
    {
        var fakes = new List<FakeCapturer>();
        await using var capture = new SharedDisplayCapture(TestDisplay, Collect(fakes), NullLogger.Instance, TimeSpan.FromMilliseconds(50));
        await using var first = capture.Subscribe();
        await using var second = capture.Subscribe();

        Assert.Single(fakes);

        fakes[0].Push();
        using var a = await first.CaptureAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        using var b = await second.CaptureAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(PixelFormat.Bgra32, a.Format);
        Assert.Equal(4, a.Width);
        Assert.Equal(4, a.Height);
        Assert.Equal((byte)1, a.GetPixels(out var strideA)[0]);
        Assert.Equal((byte)1, b.GetPixels(out var strideB)[0]);
        Assert.Equal(strideA, strideB);
    }

    [Fact]
    public async Task Cursor_updates_reach_every_subscriber()
    {
        var fakes = new List<FakeCapturer>();
        await using var capture = new SharedDisplayCapture(TestDisplay, Collect(fakes), NullLogger.Instance, TimeSpan.FromMilliseconds(50));
        await using var first = capture.Subscribe();
        await using var second = capture.Subscribe();

        var firstPositions = new List<CursorPosition>();
        var secondPositions = new List<CursorPosition>();
        var firstShapes = new List<CursorShape>();
        var secondShapes = new List<CursorShape>();
        first.CursorMoved += (_, p) => firstPositions.Add(p);
        second.CursorMoved += (_, p) => secondPositions.Add(p);
        first.CursorChanged += (_, s) => firstShapes.Add(s);
        second.CursorChanged += (_, s) => secondShapes.Add(s);

        fakes[0].ChangeCursorShape();
        fakes[0].MoveCursor(3, 7);
        fakes[0].Push();
        using (await first.CaptureAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken))
        {
        }

        using (await second.CaptureAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken))
        {
        }

        Assert.Equal(new[] { new CursorPosition(3, 7, true) }, firstPositions);
        Assert.Equal(new[] { new CursorPosition(3, 7, true) }, secondPositions);
        Assert.Single(firstShapes);
        Assert.Single(secondShapes);
        Assert.Equal(1, firstShapes[0].Width);
        Assert.Equal(1, secondShapes[0].Width);
    }

    /// <summary>
    /// Disconnecting and reconnecting used to show exactly the picture from before the gap: the newest frame was kept
    /// across the idle stop and handed to the next subscriber, and the duplication under it was never rebuilt. A
    /// reconnecting controller now gets a rebuilt capturer instead, so it sees the desktop as it is now.
    /// </summary>
    [Fact]
    public async Task A_new_subscriber_after_an_idle_gap_is_not_shown_the_previous_picture()
    {
        var fakes = new List<FakeCapturer>();
        await using var capture = new SharedDisplayCapture(TestDisplay, Collect(fakes), NullLogger.Instance, TimeSpan.FromMilliseconds(50));

        var first = capture.Subscribe();
        fakes[0].Push();
        using (await first.CaptureAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken))
        {
        }

        await first.DisposeAsync();

        // Past the idle grace, so the loop has stopped and the duplication it opened may be out of date.
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        await using var next = capture.Subscribe();
        using var replay = await next.CaptureAsync(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        Assert.Null(replay);

        await WaitForAsync(() => fakes.Count >= 2);
        fakes[^1].Push();
        using var fresh = await next.CaptureAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.NotNull(fresh);
        Assert.True(fakes.Count >= 2);
    }

    /// <summary>
    /// A capture object that throws once is what a lost graphics device looks like. Ending the loop on it left every
    /// controller of that display frozen until the host process restarted, because nothing else would start it again.
    /// </summary>
    [Fact]
    public async Task A_capturer_that_fails_is_replaced_and_the_loop_recovers()
    {
        var fakes = new List<FakeCapturer>();
        IScreenCapturer Create(DisplayInfo display)
        {
            var fake = new FakeCapturer { Fail = fakes.Count == 0 };
            fakes.Add(fake);
            return fake;
        }

        await using var capture = new SharedDisplayCapture(TestDisplay, Create, NullLogger.Instance, TimeSpan.FromMilliseconds(50));
        await using var subscription = capture.Subscribe();

        await WaitForAsync(() => fakes.Count >= 2);
        Assert.True(fakes[0].Disposed);

        fakes[^1].Push();
        using var frame = await subscription.CaptureAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.NotNull(frame);
        Assert.False(capture.IsFaulted);
    }

    private static Func<DisplayInfo, IScreenCapturer> Collect(List<FakeCapturer> fakes) => _ =>
    {
        var fake = new FakeCapturer();
        fakes.Add(fake);
        return fake;
    };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the capture loop.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Stands in for the DXGI capturer: hand-shaped frames, a cursor, a failure mode and a count of instances.</summary>
    private sealed class FakeCapturer : IScreenCapturer
    {
        private readonly SemaphoreSlim _frames = new(0);
        private int _frameNumber;

        public DisplayInfo Display => TestDisplay;

        /// <summary>Throws on every capture, the way a removed device does.</summary>
        public bool Fail { get; init; }

        public bool Disposed { get; private set; }

        public event EventHandler<CursorShape>? CursorChanged;

        public event EventHandler<CursorPosition>? CursorMoved;

        public void Push() => _frames.Release();

        public void MoveCursor(int x, int y) => CursorMoved?.Invoke(this, new CursorPosition(x, y, Visible: true));

        public void ChangeCursorShape() =>
            CursorChanged?.Invoke(this, new CursorShape(1, 1, 0, 0, new byte[] { 0, 0, 255, 255 }));

        public ValueTask<VideoFrame?> CaptureAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("The graphics device was removed.");
            }

            if (!_frames.Wait(timeout, cancellationToken))
            {
                return ValueTask.FromResult<VideoFrame?>(null);
            }

            var number = Interlocked.Increment(ref _frameNumber);
            var frame = new MemoryVideoFrame(4, 4, PixelFormat.Bgra32, TimeSpan.FromMilliseconds(number));
            frame.Buffer.Fill((byte)number);
            return ValueTask.FromResult<VideoFrame?>(frame);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _frames.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
