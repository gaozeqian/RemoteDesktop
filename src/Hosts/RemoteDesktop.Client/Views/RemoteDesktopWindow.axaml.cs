using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Client.Input;
using RemoteDesktop.Client.ViewModels;
using PointerButton = RemoteDesktop.Abstractions.Input.PointerButton;

namespace RemoteDesktop.Client.Views;

/// <summary>
/// Shows the remote screen and turns local pointer / keyboard events into remote input. Rendering copies the newest
/// decoded picture into a <see cref="WriteableBitmap"/>; at most one copy is queued at a time, so the UI thread never
/// falls behind the stream.
/// </summary>
#pragma warning disable CA1001 // A window is not IDisposable; its resources are released in OnClosed.
public sealed partial class RemoteDesktopWindow : Window
#pragma warning restore CA1001
{
    private RemoteDesktopViewModel? _viewModel;
    private WriteableBitmap? _bitmap;
    private int _updateQueued;

    public RemoteDesktopWindow()
    {
        InitializeComponent();

        // Tunnel so Tab, arrows, Alt etc. reach the remote side instead of driving local focus or menus.
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKey, RoutingStrategies.Tunnel);
        Screen.PointerMoved += OnPointerMoved;
        Screen.PointerPressed += OnPointerPressed;
        Screen.PointerReleased += OnPointerReleased;
        Screen.PointerWheelChanged += OnPointerWheel;
        Opened += (_, _) => Screen.Focus();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null || DataContext is not RemoteDesktopViewModel viewModel)
        {
            return;
        }

        _viewModel = viewModel;
        viewModel.Viewer.StreamInfoChanged += (_, _) => Dispatcher.UIThread.Post(RecreateBitmap);
        viewModel.Viewer.FrameReady += (_, _) => QueueUpdate();
        viewModel.Closed += (_, _) => Close();
        RecreateBitmap();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _bitmap?.Dispose();
        _bitmap = null;
        if (_viewModel is { } viewModel)
        {
            _ = viewModel.CloseAsync();
        }
    }

    private void RecreateBitmap()
    {
        if (_viewModel?.Viewer.StreamInfo is not { } info)
        {
            return;
        }

        if (_bitmap is null || _bitmap.PixelSize.Width != info.Width || _bitmap.PixelSize.Height != info.Height)
        {
            var previous = _bitmap;
            _bitmap = new WriteableBitmap(new PixelSize(info.Width, info.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            Screen.Source = _bitmap;
            previous?.Dispose();
        }
    }

    private void QueueUpdate()
    {
        if (Interlocked.Exchange(ref _updateQueued, 1) == 0)
        {
            Dispatcher.UIThread.Post(UpdateBitmap, DispatcherPriority.Render);
        }
    }

    private unsafe void UpdateBitmap()
    {
        Volatile.Write(ref _updateQueued, 0);
        if (_bitmap is null || _viewModel is null)
        {
            return;
        }

        using (var buffer = _bitmap.Lock())
        {
            var span = new Span<byte>((void*)buffer.Address, buffer.RowBytes * buffer.Size.Height);
            if (_viewModel.Viewer.CopyLatestFrame(span, buffer.RowBytes, buffer.Size.Width, buffer.Size.Height) < 0)
            {
                return;
            }
        }

        Screen.InvalidateVisual();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (_viewModel is not { CanControl: true })
        {
            return;
        }

        e.Handled = true;
        if (ScanCodeMap.TryGet(e.PhysicalKey, out var scanCode, out var extended))
        {
            _viewModel.SendInput(new KeyEvent(scanCode, extended, e.RoutedEvent == KeyDownEvent));
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (TryMap(e.GetPosition(Screen), out var displayId, out var x, out var y))
        {
            _viewModel!.SendInput(new PointerMoveEvent(displayId, x, y));
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Screen.Focus();
        e.Pointer.Capture(Screen);
        SendButton(e, e.GetCurrentPoint(Screen).Properties.PointerUpdateKind);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        SendButton(e, e.GetCurrentPoint(Screen).Properties.PointerUpdateKind);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (TryMap(e.GetPosition(Screen), out var displayId, out var x, out var y))
        {
            // Avalonia reports notches; Windows expects multiples of WHEEL_DELTA (120).
            _viewModel!.SendInput(new PointerWheelEvent(displayId, x, y, (int)Math.Round(e.Delta.X * 120), (int)Math.Round(e.Delta.Y * 120)));
            e.Handled = true;
        }
    }

    private void SendButton(PointerEventArgs e, PointerUpdateKind kind)
    {
        var (button, down) = kind switch
        {
            PointerUpdateKind.LeftButtonPressed => (PointerButton.Left, true),
            PointerUpdateKind.LeftButtonReleased => (PointerButton.Left, false),
            PointerUpdateKind.RightButtonPressed => (PointerButton.Right, true),
            PointerUpdateKind.RightButtonReleased => (PointerButton.Right, false),
            PointerUpdateKind.MiddleButtonPressed => (PointerButton.Middle, true),
            PointerUpdateKind.MiddleButtonReleased => (PointerButton.Middle, false),
            PointerUpdateKind.XButton1Pressed => (PointerButton.X1, true),
            PointerUpdateKind.XButton1Released => (PointerButton.X1, false),
            PointerUpdateKind.XButton2Pressed => (PointerButton.X2, true),
            PointerUpdateKind.XButton2Released => (PointerButton.X2, false),
            _ => ((PointerButton?)null, false),
        };

        if (button is { } pressed && TryMap(e.GetPosition(Screen), out var displayId, out var x, out var y))
        {
            _viewModel!.SendInput(new PointerButtonEvent(displayId, x, y, pressed, down));
        }
    }

    /// <summary>Maps a point in the image control to normalized remote coordinates, honoring the letterbox.</summary>
    private bool TryMap(Point point, out string displayId, out double x, out double y)
    {
        displayId = string.Empty;
        x = y = 0;
        if (_viewModel is not { CanControl: true } || _viewModel.Viewer.StreamInfo is not { } info)
        {
            return false;
        }

        var bounds = Screen.Bounds.Size;
        var scale = Math.Min(bounds.Width / info.Width, bounds.Height / info.Height);
        if (scale <= 0)
        {
            return false;
        }

        var offsetX = (bounds.Width - (info.Width * scale)) / 2;
        var offsetY = (bounds.Height - (info.Height * scale)) / 2;
        x = Math.Clamp((point.X - offsetX) / (info.Width * scale), 0, 1);
        y = Math.Clamp((point.Y - offsetY) / (info.Height * scale), 0, 1);
        displayId = info.DisplayId;
        return true;
    }
}
