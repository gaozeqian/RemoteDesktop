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
            return new DxgiScreenCapturer(display, logger);
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
/// <para>
/// Losing access (resolution change, desktop switch to UAC / lock screen) recreates the duplication; while that is
/// impossible (secure desktop from a user process, until milestone M3) captures simply return no frame.
/// </para>
/// <para>
/// A removed or reset graphics device is different: nothing on that device ever works again, so the failure is
/// reported through <see cref="IsFaulted"/> and the whole device is rebuilt instead of being retried. Retrying it was
/// how a driver reset used to leave every controller of a display on the same frozen picture until the host process
/// was restarted. The mouse cursor is not part of the image; cursor rendering arrives in M4.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class DxgiScreenCapturer : IScreenCapturer
{
    private const int WaitTimeout = unchecked((int)0x887A0027);
    private const int AccessLost = unchecked((int)0x887A0026);
    private const int DeviceRemoved = unchecked((int)0x887A0005);
    private const int DeviceReset = unchecked((int)0x887A0007);
    private const int MaxFirstFrameRestarts = 3;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FaultLogInterval = TimeSpan.FromSeconds(10);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ILogger _logger;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private int _width;
    private int _height;
    private bool _mapped;
    private bool _firstFrame = true;
    private bool _faulted;
    private int _firstFrameRestarts;
    private TimeSpan _nextRetry;
    private TimeSpan _initializedAt;
    private TimeSpan _lastFaultLog = TimeSpan.MinValue;

    public DxgiScreenCapturer(DisplayInfo display, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        Display = display;
        _logger = logger;
        Initialize();
    }

    public DisplayInfo Display { get; }

    public bool IsFaulted => _faulted;

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

        // Set together with the duplication and cleared together with it, so this is not null here.
        var duplication = _duplication!;
        var result = duplication.AcquireNextFrame((uint)Math.Max(0, timeout.TotalMilliseconds), out var info, out var resource);
        if (result.Code == WaitTimeout)
        {
            // A fresh duplication normally delivers the current image at once; if it does not, start over rather
            // than wait for the screen to change — but only a few times. A desktop nobody is touching has nothing
            // to deliver, and rebuilding the device every second for it is worse than simply waiting.
            if (_firstFrame && _clock.Elapsed - _initializedAt > FirstFrameTimeout && _firstFrameRestarts < MaxFirstFrameRestarts)
            {
                _firstFrameRestarts++;
                if (_firstFrameRestarts == MaxFirstFrameRestarts)
                {
                    LogNoFirstFrame(_logger, Display.Id);
                }

                ReleaseDuplication();
            }

            return ValueTask.FromResult<VideoFrame?>(null);
        }

        if (result.Code == AccessLost)
        {
            // UAC, the lock screen or a display mode change: this duplication is dead, and only a new one works.
            Fault("The desktop duplication was lost.", result.Code, timeout);
            return ValueTask.FromResult<VideoFrame?>(null);
        }

        if (!result.Success)
        {
            // A removed or reset device never comes back on the same object: throw the whole device away instead of
            // calling AcquireNextFrame on it forever.
            Fault(
                result.Code == DeviceRemoved || result.Code == DeviceReset
                    ? "The graphics device was removed."
                    : "Acquiring the next frame failed.",
                result.Code,
                timeout);
            return ValueTask.FromResult<VideoFrame?>(null);
        }

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
            duplication.ReleaseFrame();
        }

        _firstFrame = false;
        _firstFrameRestarts = 0;
        _faulted = false;
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

    /// <summary>
    /// Releases everything, so the next capture builds a new device and duplication. Called between frames by the
    /// shared capture loop, which is the only thread that touches this object.
    /// </summary>
    public void Reset()
    {
        ReleaseAll();
        _nextRetry = TimeSpan.Zero;
        _firstFrameRestarts = 0;
        _faulted = true;
    }

    public ValueTask DisposeAsync()
    {
        ReleaseAll();
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
            ReleaseAll();
            Initialize();
            _faulted = false;
            return true;
        }
        catch (Exception ex) when (ex is SharpGenException or InvalidOperationException)
        {
            // Reported, not swallowed: a display that cannot be duplicated (the secure desktop, an unplugged
            // monitor) used to look exactly like a static screen in the log, i.e. like nothing at all.
            ReleaseAll();
            _nextRetry = _clock.Elapsed + RetryInterval;
            _faulted = true;
            LogRebuildFailed(_logger, Display.Id, ex);
            return false;
        }
    }

    /// <summary>Drops the duplication and the device: for anything a rebuild is the only cure for.</summary>
    private void Fault(string reason, int code, TimeSpan timeout)
    {
        ReleaseAll();
        _faulted = true;

        // The first failure is always worth a line; a device that keeps failing is not worth one every 100 ms.
        if (_lastFaultLog == TimeSpan.MinValue || _clock.Elapsed - _lastFaultLog >= FaultLogInterval)
        {
            _lastFaultLog = _clock.Elapsed;
            LogFault(_logger, Display.Id, reason, code);
        }

        Thread.Sleep(timeout);
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

    private void ReleaseAll()
    {
        _mapped = false;
        ReleaseDuplication();
        _staging?.Dispose();
        _staging = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
    }

    private void Unmap()
    {
        if (!_mapped)
        {
            return;
        }

        _mapped = false;

        // After a fault the context is already gone; there is nothing left to unmap, and touching it would throw
        // out of a frame's Dispose.
        if (_context is not null && _staging is not null)
        {
            _context.Unmap(_staging, 0);
        }
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Capture of display {Display} failed: {Reason} (0x{Code:X8}). Rebuilding it.")]
    private static partial void LogFault(ILogger logger, string display, string reason, int code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not rebuild the capture of display {Display}; retrying.")]
    private static partial void LogRebuildFailed(ILogger logger, string display, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Display {Display} delivered no first picture; waiting for the screen to change instead of rebuilding it again.")]
    private static partial void LogNoFirstFrame(ILogger logger, string display);
}
