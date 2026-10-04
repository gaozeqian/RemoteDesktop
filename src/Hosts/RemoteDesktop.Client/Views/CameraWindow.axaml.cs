using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using RemoteDesktop.Client.ViewModels;
using RemoteDesktop.Features.Camera;

namespace RemoteDesktop.Client.Views;

/// <summary>Shows the newest decoded camera picture; like the desktop window, at most one copy is queued at a time.</summary>
#pragma warning disable CA1001 // A window is not IDisposable; its bitmap is released in OnClosed.
public sealed partial class CameraWindow : Window
#pragma warning restore CA1001
{
    private CameraWindowViewModel? _viewModel;
    private CameraViewer? _viewer;
    private WriteableBitmap? _bitmap;
    private int _updateQueued;

    public CameraWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null || DataContext is not CameraWindowViewModel viewModel)
        {
            return;
        }

        _viewModel = viewModel;
        viewModel.ViewerChanged += (_, _) => Dispatcher.UIThread.Post(AttachViewer);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_viewer is not null)
        {
            _viewer.FrameReady -= OnFrameReady;
        }

        Picture.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private void AttachViewer()
    {
        if (_viewer is not null)
        {
            _viewer.FrameReady -= OnFrameReady;
        }

        _viewer = _viewModel?.Viewer;
        if (_viewer is null)
        {
            Picture.Source = null;
            _bitmap?.Dispose();
            _bitmap = null;
            return;
        }

        _viewer.FrameReady += OnFrameReady;
    }

    private void OnFrameReady(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _updateQueued, 1) == 0)
        {
            Dispatcher.UIThread.Post(UpdateBitmap, DispatcherPriority.Render);
        }
    }

    private unsafe void UpdateBitmap()
    {
        Volatile.Write(ref _updateQueued, 0);
        if (_viewer?.Details is not { } details)
        {
            return;
        }

        if (_bitmap is null || _bitmap.PixelSize.Width != details.Width || _bitmap.PixelSize.Height != details.Height)
        {
            var previous = _bitmap;
            _bitmap = new WriteableBitmap(new PixelSize(details.Width, details.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            Picture.Source = _bitmap;
            previous?.Dispose();
        }

        using var buffer = _bitmap.Lock();
        _viewer.CopyLatestFrame(new Span<byte>((void*)buffer.Address, buffer.RowBytes * buffer.Size.Height), buffer.RowBytes, details.Width, details.Height);
        Picture.InvalidateVisual();
    }
}
