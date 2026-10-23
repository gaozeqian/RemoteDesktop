using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Media;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteDesktop.Platform.Windows.Display;

[SupportedOSPlatform("windows")]
public sealed partial class DxgiScreenCapturerFactory(ILoggerFactory loggers) : IScreenCapturerFactory, IAsyncDisposable
{
    private const int SmRemoteSession = 0x1000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, SharedDisplayCapture> _captures = new(StringComparer.Ordinal);

    /// <summary>Releases the duplication every display share holds. The container calls this when the host stops.</summary>
    public async ValueTask DisposeAsync()
    {
        SharedDisplayCapture[] captures;
        lock (_gate)
        {
            captures = [.. _captures.Values];
            _captures.Clear();
        }

        foreach (var capture in captures)
        {
            await capture.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns a subscription, not a private duplication. Windows permits one duplication interface per output per
    /// process, so a second session must share the first one's; see <see cref="SharedDisplayCapture"/>.
    /// </summary>
    public IScreenCapturer Create(DisplayInfo display)
    {
        ArgumentNullException.ThrowIfNull(display);

        SharedDisplayCapture capture;
        lock (_gate)
        {
            if (!_captures.TryGetValue(display.Id, out capture!))
            {
                capture = new SharedDisplayCapture(display, CreateCapturer, loggers.CreateLogger<SharedDisplayCapture>());
                _captures[display.Id] = capture;
            }
        }

        return capture.Subscribe();
    }

    /// <summary>
    /// Desktop Duplication is unavailable over Remote Desktop, and some adapters cannot duplicate their output at
    /// all. Both cases fall back to the GDI copier rather than leaving the controller on a black screen forever.
    /// </summary>
    private IScreenCapturer CreateCapturer(DisplayInfo display)
    {
        var logger = loggers.CreateLogger<DxgiScreenCapturerFactory>();
        if (GetSystemMetrics(SmRemoteSession) != 0)
        {
            LogRemoteSession(logger, display.Id);
            return new GdiScreenCapturer(display);
        }

        try
        {
            return new DxgiScreenCapturer(display);
        }
        catch (Exception ex) when (ex is SharpGenException or InvalidOperationException)
        {
            LogDuplicationUnavailable(logger, display.Id, ex);
            return new GdiScreenCapturer(display);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LoggerMessage(Level = LogLevel.Information, Message = "Capturing display {Display} with GDI: this is a Remote Desktop session.")]
    private static partial void LogRemoteSession(ILogger logger, string display);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Desktop Duplication is unavailable for display {Display}; falling back to GDI.")]
    private static partial void LogDuplicationUnavailable(ILogger logger, string display, Exception exception);
}

/// <summary>
/// Desktop Duplication capture. Each changed frame is copied into a staging texture and handed out mapped, so the
/// consumer reads the pixels without another copy; the next capture is only possible once that frame is disposed.
/// </summary>
/// <remarks>
/// Losing access (resolution change, desktop switch to UAC / lock screen) recreates the duplication; while that is
/// impossible (secure desktop from a user process, until milestone M3) captures simply return no frame.
/// The mouse cursor is not part of the image; cursor rendering arrives in M4.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class DxgiScreenCapturer : IScreenCapturer
{
    private const int WaitTimeout = unchecked((int)0x887A0027);
    private const int AccessLost = unchecked((int)0x887A0026);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(1);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private int _width;
    private int _height;
    private bool _mapped;
    private bool _firstFrame = true;
    private TimeSpan _nextRetry;
    private TimeSpan _initializedAt;

    public DxgiScreenCapturer(DisplayInfo display)
    {
        Display = display;
        Initialize();
    }

    public DisplayInfo Display { get; }

    public event EventHandler<CursorShape>? CursorChanged;

    public event EventHandler<CursorPosition>? CursorMoved;

    public ValueTask<VideoFrame?> CaptureAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_mapped)
        {
            throw new InvalidOperationException("Dispose the previous frame before capturing the next one.");
        }

        if (_duplication is null && !TryReinitialize())
        {
            Thread.Sleep(timeout);
            return ValueTask.FromResult<VideoFrame?>(null);
        }

        var result = _duplication!.AcquireNextFrame((uint)Math.Max(0, timeout.TotalMilliseconds), out var info, out var resource);
        if (result.Code == WaitTimeout)
        {
            // A fresh duplication normally delivers the current image at once; if it does not, start over rather
            // than wait for the screen to change.
            if (_firstFrame && _clock.Elapsed - _initializedAt > FirstFrameTimeout)
            {
                ReleaseDuplication();
            }

            return ValueTask.FromResult<VideoFrame?>(null);
        }

        if (result.Code == AccessLost)
        {
            ReleaseDuplication();
            return ValueTask.FromResult<VideoFrame?>(null);
        }

        result.CheckError();
        try
        {
            RaiseCursorEvents(info);

            // LastPresentTime 0 means only the pointer moved; the image is unchanged.
            if (info.LastPresentTime == 0 && !_firstFrame)
            {
                return ValueTask.FromResult<VideoFrame?>(null);
            }

            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            _context!.CopyResource(_staging!, texture);
        }
        finally
        {
            resource.Dispose();
            _duplication.ReleaseFrame();
        }

        _firstFrame = false;
        var mapped = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        _mapped = true;
        return ValueTask.FromResult<VideoFrame?>(new MappedFrame(this, mapped.DataPointer, (int)mapped.RowPitch)
        {
            Width = _width,
            Height = _height,
            Format = PixelFormat.Bgra32,
            Timestamp = _clock.Elapsed,
        });
    }

    public ValueTask DisposeAsync()
    {
        if (_mapped)
        {
            Unmap();
        }

        ReleaseDuplication();
        _staging?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void Initialize()
    {
        var found = DxgiDisplayEnumerator.Find(Display.Id) ?? throw new InvalidOperationException($"Display {Display.Id} not found.");
        using var adapter = found.Adapter;
        using var output = found.Output;

        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1], out _device, out _context).CheckError();

        using var output1 = output.QueryInterface<IDXGIOutput1>();
        _duplication = output1.DuplicateOutput(_device);
        var mode = _duplication.Description.ModeDescription;
        if (_staging is null || mode.Width != _width || mode.Height != _height)
        {
            _staging?.Dispose();
            _width = (int)mode.Width;
            _height = (int)mode.Height;
            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = mode.Width,
                Height = mode.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
        }

        _firstFrame = true;
        _initializedAt = _clock.Elapsed;
    }

    private bool TryReinitialize()
    {
        if (_clock.Elapsed < _nextRetry)
        {
            return false;
        }

        try
        {
            _staging?.Dispose();
            _staging = null;
            _context?.Dispose();
            _device?.Dispose();
            Initialize();
            return true;
        }
        catch (Exception ex) when (ex is SharpGenException or InvalidOperationException)
        {
            _nextRetry = _clock.Elapsed + RetryInterval;
            return false;
        }
    }

    // Must run while the frame is still held: the pointer shape is only available until ReleaseFrame.
    private unsafe void RaiseCursorEvents(in OutduplFrameInfo info)
    {
        if (info.PointerShapeBufferSize > 0 && CursorChanged is { } shapeChanged)
        {
            var buffer = new byte[info.PointerShapeBufferSize];
            fixed (byte* pointer = buffer)
            {
                if (_duplication!.GetFramePointerShape((uint)buffer.Length, (nint)pointer, out _, out var shape).Success &&
                    PointerShapeConverter.Convert(shape.Type, (int)shape.Width, (int)shape.Height, (int)shape.Pitch, buffer) is var (width, height, bgra))
                {
                    shapeChanged(this, new CursorShape(width, height, shape.HotSpot.X, shape.HotSpot.Y, bgra));
                }
            }
        }

        if (info.LastMouseUpdateTime != 0)
        {
            var position = info.PointerPosition;
            CursorMoved?.Invoke(this, new CursorPosition(position.Position.X, position.Position.Y, position.Visible));
        }
    }

    private void ReleaseDuplication()
    {
        _duplication?.Dispose();
        _duplication = null;
    }

    private void Unmap()
    {
        _context!.Unmap(_staging!, 0);
        _mapped = false;
    }

    private sealed unsafe class MappedFrame(DxgiScreenCapturer owner, nint pixels, int rowPitch) : VideoFrame
    {
        private bool _disposed;

        public override ReadOnlySpan<byte> GetPixels(out int stride)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            stride = rowPitch;
            return new ReadOnlySpan<byte>((void*)pixels, rowPitch * Height);
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                owner.Unmap();
            }

            base.Dispose(disposing);
        }
    }
}
