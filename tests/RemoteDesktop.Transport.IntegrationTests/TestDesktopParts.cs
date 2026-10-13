using System.Collections.Concurrent;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Abstractions.Media;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>Shared size of the synthetic primary screen used by the desktop tests.</summary>
internal static class TestScreen
{
    public const int Width = 640;
    public const int Height = 360;
}

/// <summary>Two synthetic monitors, the second smaller so a switch is visible as a change of resolution.</summary>
internal sealed class FakeDisplays : IDisplayEnumerator
{
    public static readonly DisplayInfo Display = new(@"\\.\FAKE1", "FAKE1", new PixelRect(0, 0, TestScreen.Width, TestScreen.Height), 1.0, true);

    public static readonly DisplayInfo Second = new(@"\\.\FAKE2", "FAKE2", new PixelRect(TestScreen.Width, 0, 480, 270), 1.0, false);

    private readonly Lock _lock = new();
    private IReadOnlyList<DisplayInfo> _displays = [Display];

    public FakeDisplays(params DisplayInfo[] displays)
    {
        if (displays.Length > 0)
        {
            _displays = displays;
        }
    }

    /// <summary>Simulates monitors being plugged in or out.</summary>
    public void Set(params DisplayInfo[] displays)
    {
        lock (_lock)
        {
            _displays = displays;
        }
    }

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        lock (_lock)
        {
            return _displays;
        }
    }
}

internal sealed class FakeCapturerFactory(int maxFrames = int.MaxValue) : IScreenCapturerFactory
{
    public IScreenCapturer Create(DisplayInfo display) => new FakeCapturer(display, maxFrames);
}

/// <summary>
/// The first display is red on the left and blue on the right, the second green and yellow, with a moving bar so every
/// frame differs. Every capture also moves the pointer, and the first one announces a 4×4 red pointer image.
/// </summary>
internal sealed class FakeCapturer(DisplayInfo display, int maxFrames) : IScreenCapturer
{
    private int _index;

    public DisplayInfo Display => display;

    public event EventHandler<CursorShape>? CursorChanged;

    public event EventHandler<CursorPosition>? CursorMoved;

    public ValueTask<VideoFrame?> CaptureAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_index >= maxFrames)
        {
            return ValueTask.FromResult<VideoFrame?>(null);
        }

        var width = display.Bounds.Width;
        var height = display.Bounds.Height;
        var second = display.Id == FakeDisplays.Second.Id;
        var frame = new MemoryVideoFrame(width, height, PixelFormat.Bgra32, TimeSpan.FromMilliseconds(_index * 33));
        var pixels = frame.Buffer;
        var bar = (_index * 8) % width;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var p = ((y * width) + x) * 4;
                var onBar = y < 20 && Math.Abs(x - bar) < 10;
                var left = x < width / 2;

                // BGRA: display 1 red | blue, display 2 green | yellow.
                var (b, g, r) = second ? (left ? (20, 230, 20) : (20, 230, 230)) : (left ? (20, 20, 230) : (230, 20, 20));
                pixels[p + 0] = (byte)(onBar ? 255 : b);
                pixels[p + 1] = (byte)(onBar ? 255 : g);
                pixels[p + 2] = (byte)(onBar ? 255 : r);
                pixels[p + 3] = 255;
            }
        }

        if (_index == 0)
        {
            var shape = new byte[4 * 4 * 4];
            for (var i = 0; i < shape.Length; i += 4)
            {
                shape[i + 2] = 255;
                shape[i + 3] = 255;
            }

            CursorChanged?.Invoke(this, new CursorShape(4, 4, 1, 1, shape));
        }

        CursorMoved?.Invoke(this, new CursorPosition(10 + (_index * 2), 20, Visible: true));
        _index++;
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

    /// <summary>False: this fake records input, and the secure attention sequence is not input it can reproduce.</summary>
    public bool SupportsSecureAttention => false;

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

/// <summary>Like the OS clipboard: setting it (by anyone) raises Changed.</summary>
internal sealed class FakeClipboard : RemoteDesktop.Abstractions.Clipboard.IClipboard
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Lock _lock = new();
    private RemoteDesktop.Abstractions.Clipboard.ClipboardContent? _content;

    public event EventHandler? Changed;

    public int SetCalls { get; private set; }

    public RemoteDesktop.Abstractions.Clipboard.ClipboardContent? Get()
    {
        lock (_lock)
        {
            return _content;
        }
    }

    public void Set(RemoteDesktop.Abstractions.Clipboard.ClipboardContent content)
    {
        lock (_lock)
        {
            _content = content;
            SetCalls++;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The user copies something: the clipboard changes without anyone calling Set.</summary>
    public void Copy(RemoteDesktop.Abstractions.Clipboard.ClipboardContent content)
    {
        lock (_lock)
        {
            _content = content;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task WaitForAsync(Func<RemoteDesktop.Abstractions.Clipboard.ClipboardContent?, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition(Get()))
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the clipboard.");
            await Task.Delay(30, Ct);
        }
    }
}

/// <summary>A 440 Hz tone, delivered in odd-sized chunks like a real device, about a second of it per capture.</summary>
internal sealed class FakeAudioCapturerFactory : RemoteDesktop.Abstractions.Audio.IAudioCapturerFactory
{
    public RemoteDesktop.Abstractions.Audio.IAudioCapturer Create() => new Capturer();

    private sealed class Capturer : RemoteDesktop.Abstractions.Audio.IAudioCapturer
    {
        public RemoteDesktop.Abstractions.Audio.AudioFormat Format => RemoteDesktop.Abstractions.Audio.AudioFormat.Standard;

        public async IAsyncEnumerable<ReadOnlyMemory<float>> CaptureAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            const int FramesPerChunk = 1000;
            for (var chunk = 0; chunk < 50; chunk++)
            {
                var samples = new float[FramesPerChunk * 2];
                for (var i = 0; i < FramesPerChunk; i++)
                {
                    var value = 0.4f * MathF.Sin(2 * MathF.PI * 440 * ((chunk * FramesPerChunk) + i) / 48_000f);
                    samples[i * 2] = value;
                    samples[(i * 2) + 1] = value;
                }

                yield return samples;
                await Task.Delay(2, cancellationToken);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class RecordingPlayerFactory : RemoteDesktop.Abstractions.Audio.IAudioPlayerFactory
{
    public RecordingPlayer Player { get; } = new();

    public RemoteDesktop.Abstractions.Audio.IAudioPlayer Create() => Player;
}

internal sealed class RecordingPlayer : RemoteDesktop.Abstractions.Audio.IAudioPlayer
{
    private readonly Lock _lock = new();
    private readonly List<float> _samples = [];

    public TimeSpan Buffered => TimeSpan.Zero;

    public int SampleCount
    {
        get
        {
            lock (_lock)
            {
                return _samples.Count;
            }
        }
    }

    /// <summary>Root mean square of everything received so far.</summary>
    public double Rms()
    {
        lock (_lock)
        {
            return _samples.Count == 0 ? 0 : Math.Sqrt(_samples.Sum(s => (double)s * s) / _samples.Count);
        }
    }

    public Task StartAsync(RemoteDesktop.Abstractions.Audio.AudioFormat format) => Task.CompletedTask;

    public void Enqueue(ReadOnlySpan<float> samples)
    {
        lock (_lock)
        {
            _samples.AddRange(samples.ToArray());
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
