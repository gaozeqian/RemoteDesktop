using System.Collections.Concurrent;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Abstractions.Media;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>Shared size of the synthetic screen used by the desktop tests.</summary>
internal static class TestScreen
{
    public const int Width = 640;
    public const int Height = 360;
}

internal sealed class FakeDisplays : IDisplayEnumerator
{
    public static readonly DisplayInfo Display = new("\\\\.\\FAKE1", "FAKE1", new PixelRect(0, 0, TestScreen.Width, TestScreen.Height), 1.0, true);

#pragma warning disable CS0067
    public event EventHandler? DisplaysChanged;
#pragma warning restore CS0067

    public IReadOnlyList<DisplayInfo> GetDisplays() => [Display];
}

internal sealed class FakeCapturerFactory(int maxFrames = int.MaxValue) : IScreenCapturerFactory
{
    public IScreenCapturer Create(DisplayInfo display) => new FakeCapturer(display, maxFrames);
}

/// <summary>Left half red, right half blue, with a moving bar so every frame differs.</summary>
internal sealed class FakeCapturer(DisplayInfo display, int maxFrames) : IScreenCapturer
{
    private int _index;

    public DisplayInfo Display => display;

#pragma warning disable CS0067
    public event EventHandler<CursorShape>? CursorChanged;
#pragma warning restore CS0067

    public ValueTask<VideoFrame?> CaptureAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_index >= maxFrames)
        {
            return ValueTask.FromResult<VideoFrame?>(null);
        }

        var frame = new MemoryVideoFrame(TestScreen.Width, TestScreen.Height, PixelFormat.Bgra32, TimeSpan.FromMilliseconds(_index * 33));
        var pixels = frame.Buffer;
        var bar = (_index++ * 8) % TestScreen.Width;
        for (var y = 0; y < TestScreen.Height; y++)
        {
            for (var x = 0; x < TestScreen.Width; x++)
            {
                var p = ((y * TestScreen.Width) + x) * 4;
                var onBar = y < 20 && Math.Abs(x - bar) < 10;
                pixels[p + 0] = (byte)(onBar ? 255 : x < TestScreen.Width / 2 ? 20 : 230);
                pixels[p + 1] = (byte)(onBar ? 255 : 20);
                pixels[p + 2] = (byte)(onBar ? 255 : x < TestScreen.Width / 2 ? 230 : 20);
                pixels[p + 3] = 255;
            }
        }

        return ValueTask.FromResult<VideoFrame?>(frame);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class RecordingInjector : IInputInjector, IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ConcurrentQueue<InputEvent> _events = new();
    private readonly SemaphoreSlim _signal = new(0);

    public IReadOnlyList<InputEvent> Events => [.. _events];

    public void Inject(InputEvent inputEvent)
    {
        _events.Enqueue(inputEvent);
        _signal.Release();
    }

    public void SendSecureAttentionSequence()
    {
    }

    public void Dispose() => _signal.Dispose();

    public async Task WaitForAsync(int count)
    {
        while (_events.Count < count)
        {
            Assert.True(await _signal.WaitAsync(TimeSpan.FromSeconds(10), Ct), $"Only {_events.Count} input events arrived.");
        }
    }
}
