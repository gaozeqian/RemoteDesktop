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
    [Fact]
    public async Task Two_subscribers_share_one_capturer_and_both_receive_the_same_frame()
    {
        var fake = new FakeCapturer();
        var creates = 0;
        await using var capture = new SharedDisplayCapture(
            fake.Display,
            _ =>
            {
                creates++;
                return fake;
            },
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(50));
        await using var first = capture.Subscribe();
        await using var second = capture.Subscribe();

        Assert.Equal(1, creates);

        fake.Push();
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
        var fake = new FakeCapturer();
        await using var capture = new SharedDisplayCapture(
            fake.Display,
            _ => fake,
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(50));
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

        fake.ChangeCursorShape();
        fake.MoveCursor(3, 7);
        fake.Push();
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

    [Fact]
    public async Task A_new_subscriber_after_an_idle_gap_reads_the_last_frame_without_a_new_capture()
    {
        var fake = new FakeCapturer();
        var creates = 0;
        await using var capture = new SharedDisplayCapture(
            fake.Display,
            _ =>
            {
                creates++;
                return fake;
            },
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(50));

        var first = capture.Subscribe();
        fake.Push();
        using (await first.CaptureAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken))
        {
        }

        await first.DisposeAsync();

        // Past the idle grace, so the loop has stopped. The duplication is still the same one.
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        await using var next = capture.Subscribe();
        using var frame = await next.CaptureAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

        Assert.NotNull(frame);
        Assert.Equal(1, creates);
        Assert.Equal((byte)1, frame.GetPixels(out _)[0]);
    }

    /// <summary>Stands in for the DXGI capturer: hand-shaped frames, a cursor, and a count of instances.</summary>
    private sealed class FakeCapturer : IScreenCapturer
    {
        private readonly SemaphoreSlim _frames = new(0);
        private int _frameNumber;

        public DisplayInfo Display { get; } = new("display-1", "Test", new PixelRect(0, 0, 4, 4), 1, IsPrimary: true);

        public event EventHandler<CursorShape>? CursorChanged;

        public event EventHandler<CursorPosition>? CursorMoved;

        public void Push() => _frames.Release();

        public void MoveCursor(int x, int y) => CursorMoved?.Invoke(this, new CursorPosition(x, y, Visible: true));

        public void ChangeCursorShape() =>
            CursorChanged?.Invoke(this, new CursorShape(1, 1, 0, 0, new byte[] { 0, 0, 255, 255 }));

        public ValueTask<VideoFrame?> CaptureAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
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
            _frames.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
