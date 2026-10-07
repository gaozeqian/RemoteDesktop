using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Features.Camera;
using RemoteDesktop.Media;
using RemoteDesktop.Platform.Windows.Recording;
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
public sealed partial class CameraWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IPeerConnection _connection;
    private readonly VideoCodecSelector _codecs;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IVideoRecorderFactory? _recorderFactory;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _recordingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private IVideoRecorder? _recorder;
    private byte[]? _recordingBuffer;
    private string? _recordingFileName;
    private CameraViewer? _viewer;

    public CameraWindowViewModel(
        IPeerConnection connection,
        VideoCodecSelector codecs,
        TimeProvider time,
        ILogger logger,
        IVideoRecorderFactory? recorderFactory = null)
    {
        _connection = connection;
        _codecs = codecs;
        _time = time;
        _logger = logger;
        if (recorderFactory is not null)
        {
            _recorderFactory = recorderFactory;
        }
        else if (OperatingSystem.IsWindows())
        {
            _recorderFactory = new MfVideoRecorderFactory();
        }

        _recordingTimer.Tick += (_, _) => UpdateRecordingDuration();
    }

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

    [ObservableProperty]
    public partial bool IsRecording { get; set; }

    [ObservableProperty]
    public partial string RecordingDurationText { get; set; } = "00:00";

    [ObservableProperty]
    public partial string RecordButtonText { get; set; } = "⏺ 录制摄像头";

    [ObservableProperty]
    public partial string? RecordingStatusMessage { get; set; }

    /// <summary>The running stream, for the view to draw. Raised on the UI thread when it starts or ends.</summary>
    public CameraViewer? Viewer => _viewer;

    public event EventHandler? ViewerChanged;

    public async Task InitializeAsync()
    {
        try
        {
            var cameras = await CameraViewer.ListCamerasAsync(_connection, CancellationToken.None);
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
            var viewer = await CameraViewer.StartAsync(_connection, _codecs, SelectedCamera.Camera.Id, SelectedMode.Mode, _time, _logger, CancellationToken.None);
            viewer.Closed += (_, _) => Dispatcher.UIThread.Post(() => _ = OnViewerClosedAsync(viewer));
            viewer.FrameReady += (_, _) => OnFrameReady();
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

    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        if (IsRecording)
        {
            await StopRecordingInternalAsync();
            return;
        }

        // FrameNumber, not just Details: the stream is described before the first picture arrives, and a recording
        // started in that gap would have nothing to write into it.
        if (_viewer?.Details is not { } details || _viewer.FrameNumber == 0)
        {
            RecordingStatusMessage = "尚未开启摄像头画面，无法录制。";
            return;
        }

        if (_recorderFactory is null)
        {
            RecordingStatusMessage = "当前系统环境不支持视频录制。";
            return;
        }

        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "RemoteDesktop");
        var filePath = RecordingFile.NextPath(folder, "Camera");
        var fileName = Path.GetFileName(filePath);

        var newRecorder = _recorderFactory.Create();
        try
        {
            newRecorder.Start(filePath, details.Width, details.Height, Math.Clamp(details.FrameRate, 1, 60), 3000);
        }
        catch (Exception ex)
        {
            // Media Foundation fails in more ways than can be listed (no encoder, codec refused the size, disk refused
            // the path). Report it: letting it escape an async command would end the process instead.
            await newRecorder.DisposeAsync();
            LogRecordingStartFailed(_logger, ex, fileName);
            RecordingStatusMessage = $"无法开始录制（{fileName}）：系统编码器不可用或路径不可写。";
            return;
        }

        // Taken before the recorder is published, so it never comes out empty and the viewer's thread cannot be in
        // the same buffer at the same time.
        FeedLatestFrame(_viewer, newRecorder);

        _recorder = newRecorder;
        _recordingFileName = fileName;
        IsRecording = true;
        RecordingDurationText = "00:00";
        RecordButtonText = "⏹ 停止录制 (00:00)";
        RecordingStatusMessage = $"正在录制: {fileName}";
        _recordingTimer.Start();
    }

    private void UpdateRecordingDuration()
    {
        if (_recorder is not { IsRecording: true } recorder)
        {
            return;
        }

        var dur = recorder.Duration;
        RecordingDurationText = dur.ToString(dur.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
        RecordButtonText = $"⏹ 停止录制 ({RecordingDurationText})";

        if (RecordingMessages.Interrupted(recorder.Failure) is { } interrupted)
        {
            // The file has stopped growing: close it and say so, rather than leave a clock counting over it.
            _recordingTimer.Stop();
            if (Interlocked.CompareExchange(ref _recorder, null, recorder) == recorder)
            {
                _ = StopAfterFailureAsync(recorder, interrupted);
            }

            return;
        }

        RecordingStatusMessage = $"正在录制: {_recordingFileName} · {recorder.WrittenFrames} 帧";
    }

    /// <summary>Closes a recording the encoder gave up on, and tells the user where it got to.</summary>
    private async Task StopAfterFailureAsync(IVideoRecorder recorder, string interrupted)
    {
        var saved = false;
        try
        {
            await recorder.StopAsync();
            await recorder.DisposeAsync();
            saved = true;
        }
        catch (Exception ex)
        {
            LogRecordingStopFailed(_logger, ex, recorder.Failure);
        }
        finally
        {
            IsRecording = false;
            RecordButtonText = "⏺ 录制摄像头";
        }

        RecordingStatusMessage = saved ? $"{interrupted} 已保存到 {recorder.OutputFilePath}" : interrupted;
    }

    private void OnFrameReady()
    {
        // Raised from the viewer's receive thread while this window may be closing on the UI thread: take the
        // references this frame uses once, so a teardown in the middle cannot become a null dereference here.
        var viewer = _viewer;
        var recorder = _recorder;
        if (!IsRecording || recorder is null || viewer is null)
        {
            return;
        }

        FeedLatestFrame(viewer, recorder);
    }

    /// <summary>
    /// Copies what the camera is showing right now into a recording: when one starts, on every decoded frame, and
    /// once more when one is stopped, so the recording runs to the moment it was stopped.
    /// </summary>
    private void FeedLatestFrame(CameraViewer viewer, IVideoRecorder recorder)
    {
        if (viewer.Details is not { } details)
        {
            return;
        }

        var rowBytes = details.Width * 4;
        var requiredSize = rowBytes * details.Height;
        // Held in a local: the viewer's thread and the UI thread can both be here, and the frame that gets recorded
        // has to be the one that was copied into.
        var buffer = _recordingBuffer;
        if (buffer is null || buffer.Length != requiredSize)
        {
            buffer = new byte[requiredSize];
            _recordingBuffer = buffer;
        }

        if (viewer.CopyLatestFrame(buffer, rowBytes, details.Width, details.Height) >= 0)
        {
            // A recorder stopped meanwhile ignores the frame; it is never disposed while a frame is in flight.
            recorder.RecordFrame(buffer, rowBytes, details.Width, details.Height);
        }
    }

    private async Task StopRecordingInternalAsync()
    {
        _recordingTimer.Stop();
        var recorder = Interlocked.Exchange(ref _recorder, null);
        try
        {
            if (recorder is not null)
            {
                // One last picture, so the recording runs to the moment it was stopped rather than to the last frame
                // the camera sent. The viewer's thread is already out of the way: the exchange above is what it checks.
                if (_viewer is { } viewer)
                {
                    FeedLatestFrame(viewer, recorder);
                }

                var savedPath = await recorder.StopAsync();
                await recorder.DisposeAsync();
                RecordingStatusMessage = $"录制已保存: {savedPath}";
            }
        }
        catch (Exception ex)
        {
            // The reason is kept: a generic sentence here is what makes this failure impossible to place.
            LogRecordingStopFailed(_logger, ex, recorder?.Failure ?? VideoRecorderFailure.None);
            RecordingStatusMessage = RecordingMessages.StopFailed(recorder?.Failure ?? VideoRecorderFailure.None, ex);
        }
        finally
        {
            IsRecording = false;
            RecordButtonText = "⏺ 录制摄像头";
        }
    }

    private async Task EndStreamAsync()
    {
        try
        {
            await StopRecordingInternalAsync();
        }
        catch (Exception ex)
        {
            // However the recording ended, the camera stream still has to be handed back to the host: skipping the
            // teardown below would leave the host believing this machine is still watching.
            LogRecordingStopFailed(_logger, ex, VideoRecorderFailure.None);
        }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not start recording the camera stream to {FileName}.")]
    private static partial void LogRecordingStartFailed(ILogger logger, Exception ex, string fileName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stopping the camera recording failed ({Failure}).")]
    private static partial void LogRecordingStopFailed(ILogger logger, Exception ex, VideoRecorderFailure failure);
}
