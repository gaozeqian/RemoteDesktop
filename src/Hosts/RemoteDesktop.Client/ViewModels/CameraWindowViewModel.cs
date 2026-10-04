using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Features.Camera;
using RemoteDesktop.Media;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Client.ViewModels;

public sealed record CameraChoice(CameraInfo Camera)
{
    public string Name => Camera.Name;
}

public sealed record CameraModeChoice(CameraMode Mode)
{
    public string Label => string.Create(CultureInfo.InvariantCulture, $"{Mode.Width}×{Mode.Height} · {Mode.FrameRate} fps");
}

/// <summary>The camera window: pick one of the host's cameras and a mode, watch the picture, see how the stream is doing.</summary>
public sealed partial class CameraWindowViewModel(
    IPeerConnection connection,
    VideoCodecSelector codecs,
    TimeProvider time,
    ILogger logger) : ObservableObject, IAsyncDisposable
{
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CameraViewer? _viewer;

    public ObservableCollection<CameraChoice> Cameras { get; } = [];

    public ObservableCollection<CameraModeChoice> Modes { get; } = [];

    [ObservableProperty]
    public partial CameraChoice? SelectedCamera { get; set; }

    [ObservableProperty]
    public partial CameraModeChoice? SelectedMode { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial bool IsStreaming { get; set; }

    /// <summary>What is wrong, or why there is no picture (empty when all is well).</summary>
    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>The running stream, for the view to draw. Raised on the UI thread when it starts or ends.</summary>
    public CameraViewer? Viewer => _viewer;

    public event EventHandler? ViewerChanged;

    public async Task InitializeAsync()
    {
        try
        {
            var cameras = await CameraViewer.ListCamerasAsync(connection, CancellationToken.None);
            foreach (var camera in cameras)
            {
                Cameras.Add(new CameraChoice(camera));
            }

            SelectedCamera = Cameras.FirstOrDefault();
            Message = Cameras.Count == 0 ? "被控端没有可用的摄像头。" : null;
        }
        catch (CameraException ex)
        {
            Message = ex.Message;
        }
    }

    partial void OnSelectedCameraChanged(CameraChoice? value)
    {
        Modes.Clear();
        if (value is null)
        {
            SelectedMode = null;
            return;
        }

        // Picture sizes are offered once each, at their best rate; more than 30 fps is not streamed.
        foreach (var mode in value.Camera.Modes.Where(m => m.FrameRate <= 30).GroupBy(m => (m.Width, m.Height)).Select(g => g.OrderByDescending(m => m.FrameRate).First()))
        {
            Modes.Add(new CameraModeChoice(mode));
        }

        SelectedMode = Modes.FirstOrDefault(m => m.Mode.Width == 1280 && m.Mode.Height == 720) ?? Modes.FirstOrDefault();
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedModeChanged(CameraModeChoice? value) => StartCommand.NotifyCanExecuteChanged();

    private bool CanStart() => !IsStreaming && SelectedCamera is not null && SelectedMode is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (SelectedCamera is null || SelectedMode is null)
        {
            return;
        }

        Message = "正在打开摄像头…";
        IsStreaming = true;
        try
        {
            var viewer = await CameraViewer.StartAsync(connection, codecs, SelectedCamera.Camera.Id, SelectedMode.Mode, time, logger, CancellationToken.None);
            viewer.Closed += (_, _) => Dispatcher.UIThread.Post(() => _ = OnViewerClosedAsync(viewer));
            _viewer = viewer;
            Message = null;
            ViewerChanged?.Invoke(this, EventArgs.Empty);
            _statusTimer.Tick += UpdateStatus;
            _statusTimer.Start();
        }
        catch (CameraException ex)
        {
            Message = ex.Message;
            IsStreaming = false;
        }
    }

    private bool CanStop() => IsStreaming;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        await EndStreamAsync();
        Message = null;
    }

    public async ValueTask DisposeAsync() => await EndStreamAsync();

    // The host ended the stream (camera unplugged, taken by another program, or the host turned it off).
    private async Task OnViewerClosedAsync(CameraViewer viewer)
    {
        if (!ReferenceEquals(viewer, _viewer))
        {
            return;
        }

        var reason = viewer.Failure;
        await EndStreamAsync();
        Message = reason ?? "摄像头画面已结束（被控端可能关闭了摄像头）。";
    }

    private async Task EndStreamAsync()
    {
        _statusTimer.Stop();
        _statusTimer.Tick -= UpdateStatus;
        var viewer = Interlocked.Exchange(ref _viewer, null);
        IsStreaming = false;
        StatusText = string.Empty;
        if (viewer is not null)
        {
            ViewerChanged?.Invoke(this, EventArgs.Empty);
            await viewer.DisposeAsync();
        }
    }

    private void UpdateStatus(object? sender, EventArgs e)
    {
        if (_viewer is not { Details: { } details } viewer)
        {
            return;
        }

        var stats = viewer.Statistics;
        var latency = stats.LatencyMilliseconds is { } ms ? $" · 延迟 {ms:0} ms" : string.Empty;
        StatusText = string.Create(
            CultureInfo.InvariantCulture,
            $"{details.CameraName} · {details.Width}×{details.Height} · {stats.FramesPerSecond:0} fps · {stats.Kbps / 1000:0.0} Mbps · {(details.HardwareEncoder ? "硬件" : "软件")}编码（{details.EncoderName}）{latency}");
    }
}
