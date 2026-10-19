using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Protocol.Desktop;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Client.ViewModels;

/// <summary>A display of the host that can be streamed.</summary>
public sealed record DisplayChoice(string Id, string Label)
{
    // The ComboBox automation peer uses ToString for the collapsed value; without this it read
    // "DisplayChoice { Id = …, Label = … }" instead of the display the user had selected.
    public override string ToString() => Label;
}

/// <summary>One remote desktop window: the video stream, the input path, audio, clipboard and the session they belong to.</summary>
public sealed partial class RemoteDesktopViewModel : ObservableObject, IAsyncDisposable
{
    private readonly RemoteSession _session;
    private readonly AudioPlayback? _audio;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _recordingTimer;
    private readonly IVideoRecorderFactory? _recorderFactory;
    private readonly ILogger _logger;
    private IVideoRecorder? _recorder;
    private byte[]? _recordingBuffer;
    private string? _recordingFileName;
    private InputSender? _input;
    private ClipboardShare? _clipboard;
    private FileManagerViewModel? _files;
    private TerminalWindowViewModel? _terminal;
    private CameraWindowViewModel? _camera;
    private readonly Func<IPeerConnection, CameraWindowViewModel>? _cameraFactory;
    private bool _updatingDisplays;
    private bool _hostAllowsControl;
    private int _closed;

    public RemoteDesktopViewModel(
        RemoteSession session,
        DesktopViewer viewer,
        InputSender? input,
        AudioPlayback? audio,
        ClipboardShare? clipboard,
        ILogger logger,
        Func<IPeerConnection, CameraWindowViewModel>? cameraFactory = null,
        IVideoRecorderFactory? recorderFactory = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(logger);

        _session = session;
        _logger = logger;
        _cameraFactory = cameraFactory;
        // Null means this platform has no recorder, which the recording command reports rather than hides behind
        // a button that cannot work. It comes from the container, so the platform layer decides — not this class —
        // and every platform layer registers one: Media Foundation on Windows, VideoToolbox on macOS, libavcodec on
        // Linux. There used to be a hardcoded Windows one behind an IsWindows() check, which meant recording
        // silently did nothing on any other platform rather than failing to compile.
        _recorderFactory = recorderFactory;
        CanUseCamera = session.Context.Grant.Allows(Permissions.Camera) && cameraFactory is not null;
        ShowCameraButton = CanUseCamera;
        Viewer = viewer;
        _input = input;
        _audio = audio;
        _clipboard = clipboard;
        _hostAllowsControl = input is not null;
        UpdateInputAccess();
        CanTransferFiles = session.Context.Grant.Allows(Permissions.FileTransfer);
        CanUseTerminal = session.Context.Grant.Allows(Permissions.Terminal);
        // Deliberately not derived from the controller's own injector the way CanControl is: whether Ctrl+Alt+Del
        // exists at all is a property of the host, and the host says so by granting or withholding the permission.
        CanSendSecureAttention = session.Context.Grant.Allows(Permissions.SecureAttention);
        HasAudio = audio is not null;
        UpdateTitle();

        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStatus());
        _statusTimer.Start();

        _recordingTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateRecordingDuration());

        viewer.StreamInfoChanged += (_, _) => Dispatcher.UIThread.Post(UpdateDisplays);
        viewer.FrameReady += (_, _) => OnFrameReady();
        viewer.Closed += (_, _) => Dispatcher.UIThread.Post(() => _ = CloseAsync());
        session.Closed += (_, _) => Dispatcher.UIThread.Post(() => _ = CloseAsync());
        session.GrantChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = OnGrantChangedAsync());
        session.InputAccessChanged += (_, _) => Dispatcher.UIThread.Post(UpdateInputAccess);
        UpdateStatus();
        UpdateDisplays();
    }

    public DesktopViewer Viewer { get; }

    /// <summary>The host's displays, when there is more than one to choose from.</summary>
    public ObservableCollection<DisplayChoice> Displays { get; } = [];

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "正在等待画面…";

    /// <summary>
    /// Whether this window captures and sends input. False in watch mode, when the host switches the session to
    /// watch-only, and while the controlled end's desktop has refused or cannot receive synthetic input.
    /// </summary>
    [ObservableProperty]
    public partial bool CanControl { get; set; }

    /// <summary>
    /// Where the controlled end's operating system stands on injecting input, as the host reports it. Null when the
    /// host has not said: an older host or a platform with no separate permission. Silence is not a refusal.
    /// </summary>
    [ObservableProperty]
    public partial InputAccessStatus? InputAccess { get; set; }

    /// <summary>What to tell the user about <see cref="InputAccess"/>; empty when there is nothing worth saying.</summary>
    [ObservableProperty]
    public partial string? InputAccessMessage { get; set; }

    /// <summary>Whether the host refused or cannot inject, so asking it again is the useful action.</summary>
    [ObservableProperty]
    public partial bool CanRetryInputAccess { get; set; }

    /// <summary>A retry has been sent and its answer has not arrived yet.</summary>
    [ObservableProperty]
    public partial bool IsInputAccessRequesting { get; set; }

    /// <summary>
    /// Whether this session may raise Ctrl+Alt+Del on the host. False against a host that cannot do it — which is not
    /// the same question as the controller's platform: a Windows controller connected to a macOS host hides the button
    /// too, and a macOS controller connected to a service-hosted Windows machine shows it.
    /// </summary>
    [ObservableProperty]
    public partial bool CanSendSecureAttention { get; set; }

    /// <summary>Whether the host lets this session reach its files. Goes false when the host takes it away mid-session.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFilesCommand))]
    public partial bool CanTransferFiles { get; set; }

    /// <summary>Whether the host lets this session open shells. Goes false when the host takes it away mid-session.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenTerminalCommand))]
    public partial bool CanUseTerminal { get; set; }

    /// <summary>
    /// Whether the host lets this session use its camera. Off by default on the host, so the button is shown (in control
    /// mode) but disabled until the person there allows it; it also goes false when they take it away mid-session.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCameraCommand))]
    public partial bool CanUseCamera { get; set; }

    [ObservableProperty]
    public partial bool ShowCameraButton { get; set; }

    [ObservableProperty]
    public partial bool HasAudio { get; set; }

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    public partial DisplayChoice? SelectedDisplay { get; set; }

    [ObservableProperty]
    public partial bool ShowDisplayPicker { get; set; }

    [ObservableProperty]
    public partial bool IsRecording { get; set; }

    [ObservableProperty]
    public partial string RecordingDurationText { get; set; } = "00:00";

    // Text-presentation symbols (●/■), not emoji (⏺/⏹): the emoji font's line metrics make the record button taller
    // than the other toolbar buttons. Keep both the idle and recording text on the same symbols.
    [ObservableProperty]
    public partial string RecordButtonText { get; set; } = "● 录制屏幕";

    [ObservableProperty]
    public partial string? RecordingStatusMessage { get; set; }

    /// <summary>Raised once when the window should close.</summary>
    public event EventHandler? Closed;

    /// <summary>The user asked for the file manager (the same instance each time while it is open).</summary>
    public event EventHandler<FileManagerViewModel>? FileManagerRequested;

    /// <summary>The file manager can no longer work (access taken away, session over) and its window should close.</summary>
    public event EventHandler? FileManagerUnavailable;

    /// <summary>The user asked for the terminal window (the same instance each time while it is open).</summary>
    public event EventHandler<TerminalWindowViewModel>? TerminalRequested;

    /// <summary>Terminals can no longer work (access taken away, session over) and the window should close.</summary>
    public event EventHandler? TerminalUnavailable;

    /// <summary>The user asked for the camera window (the same instance each time while it is open).</summary>
    public event EventHandler<CameraWindowViewModel>? CameraRequested;

    /// <summary>The host took the camera away; the window shows nothing useful any more and should close.</summary>
    public event EventHandler? CameraUnavailable;

    public void SendInput(InputEvent inputEvent) => _input?.Send(inputEvent);

    partial void OnSelectedDisplayChanged(DisplayChoice? value)
    {
        if (!_updatingDisplays && value is not null && value.Id != Viewer.StreamInfo?.DisplayId)
        {
            _ = Viewer.SelectDisplayAsync(value.Id, CancellationToken.None);
        }
    }

    [RelayCommand]
    private Task DisconnectAsync() => CloseAsync();

    [RelayCommand]
    private void SendSecureAttention() => SendInput(new SecureAttentionEvent());

    [RelayCommand(CanExecute = nameof(CanTransferFiles))]
    private void OpenFiles()
    {
        if (_files is null)
        {
            _files = new FileManagerViewModel(_session.Context.Connection);
            _ = _files.InitializeAsync();
        }

        FileManagerRequested?.Invoke(this, _files);
    }

    [RelayCommand(CanExecute = nameof(CanUseTerminal))]
    private void OpenTerminal()
    {
        if (_terminal is null)
        {
            _terminal = new TerminalWindowViewModel(_session.Context.Connection);
            _ = _terminal.InitializeAsync();
        }

        TerminalRequested?.Invoke(this, _terminal);
    }

    [RelayCommand(CanExecute = nameof(CanUseCamera))]
    private void OpenCamera()
    {
        if (_camera is null && _cameraFactory is not null)
        {
            _camera = _cameraFactory(_session.Context.Connection);
            _ = _camera.InitializeAsync();
        }

        if (_camera is not null)
        {
            CameraRequested?.Invoke(this, _camera);
        }
    }

    /// <summary>Called when the camera window closed (or the host took the camera away): stops the stream, which frees the camera on the host.</summary>
    public async Task ReleaseCameraAsync()
    {
        var camera = Interlocked.Exchange(ref _camera, null);
        if (camera is not null)
        {
            await camera.DisposeAsync();
        }
    }

    /// <summary>Called when the user closed the terminal window: ends its shells on the host.</summary>
    public async Task ReleaseTerminalAsync()
    {
        var terminal = Interlocked.Exchange(ref _terminal, null);
        if (terminal is not null)
        {
            await terminal.DisposeAsync();
        }
    }

    /// <summary>Called when the user closed the file manager window: ends its transfers (partial files stay, so they can be continued later).</summary>
    public async Task ReleaseFileManagerAsync()
    {
        var files = Interlocked.Exchange(ref _files, null);
        if (files is not null)
        {
            await files.DisposeAsync();
        }
    }

    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        if (IsRecording)
        {
            _recordingTimer.Stop();
            var recorder = Interlocked.Exchange(ref _recorder, null);
            try
            {
                if (recorder is not null)
                {
                    // Take the picture one last time, so the recording runs to the moment it was stopped rather than
                    // to the last time the screen changed. The decode thread is already out of the way: the exchange
                    // above is what it checks.
                    FeedLatestFrame(recorder);
                    var savedPath = await recorder.StopAsync();
                    await recorder.DisposeAsync();
                    RecordingStatusMessage = $"录制已保存: {savedPath}";
                }
            }
            catch (Exception ex)
            {
                // Failing to finalize must not leave the button stuck on "recording", nor take the session down.
                // The reason is kept: a generic sentence here is what makes this failure impossible to place.
                LogRecordingStopFailed(_logger, ex, recorder?.Failure ?? VideoRecorderFailure.None);
                RecordingStatusMessage = RecordingMessages.StopFailed(recorder?.Failure ?? VideoRecorderFailure.None, ex);
            }
            finally
            {
                IsRecording = false;
                RecordButtonText = "● 录制屏幕";
            }

            return;
        }

        // FrameNumber, not just StreamInfo: the host announces the stream before it sends a picture, and a recording
        // started in that gap - or while the picture is still - would have nothing to write into it.
        if (Viewer.StreamInfo is not { } info || Viewer.FrameNumber == 0)
        {
            RecordingStatusMessage = "尚未接收到画面，无法开始录制。";
            return;
        }

        if (_recorderFactory is null)
        {
            RecordingStatusMessage = "当前系统环境不支持视频录制。";
            return;
        }

        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "RemoteDesktop");
        string? fileName = null;
        IVideoRecorder? newRecorder = null;
        try
        {
            var filePath = RecordingFile.NextPath(folder, "Screen");
            fileName = Path.GetFileName(filePath);
            newRecorder = _recorderFactory.Create();
            newRecorder.Start(filePath, info.Width, info.Height, 30, 4000);
        }
        catch (Exception ex)
        {
            // Media Foundation fails in more ways than can be listed (no encoder, codec refused the size, disk
            // refused the path). Report it: letting it escape an async command would end the process instead. The
            // exception is logged with the path: the message the window shows has room for a cause, not a stack.
            if (newRecorder is not null)
            {
                await newRecorder.DisposeAsync();
            }

            LogRecordingStartFailed(_logger, ex, string.IsNullOrEmpty(fileName) ? folder : fileName);
            RecordingStatusMessage = $"无法开始录制（{fileName ?? "录像文件夹"}）：系统编码器不可用或路径不可写。";
            return;
        }

        // Taken before the recorder is published: a still screen would otherwise produce a recording with nothing in
        // it at all, and this way the decoder's thread cannot be in the same buffer at the same time.
        FeedLatestFrame(newRecorder);

        _recorder = newRecorder;
        _recordingFileName = fileName;
        IsRecording = true;
        RecordingDurationText = "00:00";
        RecordButtonText = "■ 停止录制 (00:00)";
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
        RecordButtonText = $"■ 停止录制 ({RecordingDurationText})";

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not start recording the screen to {FileName}.")]
    private static partial void LogRecordingStartFailed(ILogger logger, Exception ex, string fileName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stopping the screen recording failed ({Failure}).")]
    private static partial void LogRecordingStopFailed(ILogger logger, Exception ex, VideoRecorderFailure failure);

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
        catch (Exception)
        {
            // The message below already says the recording ended early; there is nothing to add.
        }
        finally
        {
            IsRecording = false;
            RecordButtonText = "● 录制屏幕";
        }

        RecordingStatusMessage = saved ? $"{interrupted} 已保存到 {recorder.OutputFilePath}" : interrupted;
    }

    private void OnFrameReady()
    {
        // This runs on the decoder's thread while the stop button runs on the UI thread: take the recorder once and
        // use that reference throughout, or a stop landing in the middle of the copy below dereferences null.
        var recorder = _recorder;
        if (!IsRecording || recorder is null)
        {
            return;
        }

        FeedLatestFrame(recorder);
    }

    /// <summary>
    /// Copies what the window is showing right now into a recording. The host sends a frame only when the picture
    /// changes, so this is what keeps a recording continuous: it is called when one starts, on every decoded frame,
    /// and once more when one is stopped, which is where a still screen would otherwise lose the most.
    /// </summary>
    private void FeedLatestFrame(IVideoRecorder recorder)
    {
        if (Viewer.StreamInfo is not { } info)
        {
            return;
        }

        var rowBytes = info.Width * 4;
        var requiredSize = rowBytes * info.Height;
        // Held in a local: the decoder's thread and the UI thread can both be here, and the frame that gets recorded
        // has to be the one that was copied into.
        var buffer = _recordingBuffer;
        if (buffer is null || buffer.Length != requiredSize)
        {
            buffer = new byte[requiredSize];
            _recordingBuffer = buffer;
        }

        if (Viewer.CopyLatestFrame(buffer, rowBytes, info.Width, info.Height) >= 0)
        {
            // A recorder stopped meanwhile ignores the frame; it is never disposed while a frame is in flight.
            recorder.RecordFrame(buffer, rowBytes, info.Width, info.Height);
        }
    }

    [RelayCommand]
    private void ToggleMute()
    {
        IsMuted = !IsMuted;
        if (_audio is not null)
        {
            _audio.Muted = IsMuted;
        }
    }

    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _statusTimer.Stop();
        Closed?.Invoke(this, EventArgs.Empty);
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _statusTimer.Stop();
        _recordingTimer.Stop();
        if (_recorder is not null)
        {
            try
            {
                await _recorder.StopAsync();
                await _recorder.DisposeAsync();
            }
            catch
            {
                // Ignore recording stop errors on forced shutdown
            }

            _recorder = null;
        }

        await ReleaseFileManagerAsync();
        await ReleaseTerminalAsync();
        await ReleaseCameraAsync();
        if (_input is not null)
        {
            await _input.DisposeAsync();
        }

        if (_clipboard is not null)
        {
            await _clipboard.DisposeAsync();
        }

        if (_audio is not null)
        {
            await _audio.DisposeAsync();
        }

        await Viewer.DisposeAsync();
        await _session.DisposeAsync();
    }

    /// <summary>Mirrors the host's input authorization into the banner and into whether input is captured at all.</summary>
    private void UpdateInputAccess()
    {
        var status = _session.InputAccess;
        InputAccess = status;
        IsInputAccessRequesting = false;

        if (!_session.Context.Grant.Allows(Permissions.ControlInput))
        {
            // The host withdrew control entirely; the operating system's answer has nothing left to govern here.
            InputAccessMessage = null;
            CanRetryInputAccess = false;
            UpdateCanControl();
            return;
        }

        (InputAccessMessage, CanRetryInputAccess) = status?.State switch
        {
            InputAccessState.Requesting =>
                ("已请求被控端授权控制。请在被控端桌面上允许系统弹出的对话框。", false),
            InputAccessState.Denied => (status.Reason is { Length: > 0 } reason
                ? $"被控端未允许控制：当前只能观看。（{reason}）"
                : "被控端未允许控制：当前只能观看。请在被控端桌面上允许后重试。", true),
            InputAccessState.Unavailable => (status.Reason is { Length: > 0 } reason
                ? $"被控端无法注入键鼠：{reason}"
                : "被控端无法注入键鼠：输入服务不可用。", true),
            _ => (null, false),
        };

        UpdateCanControl();
    }

    private void UpdateCanControl()
    {
        // Denied and Unavailable are both "input would be sent and thrown away"; NotRequested still allows it because
        // the first event is what asks the desktop. The host grant is a separate gate that the host enforces.
        var blocked = _session.InputAccess?.State is InputAccessState.Denied or InputAccessState.Unavailable;
        CanControl = _hostAllowsControl && _input is not null && !blocked;
    }

    /// <summary>Asks the host to put the desktop's permission question up again.</summary>
    [RelayCommand]
    private async Task RetryInputAccessAsync()
    {
        if (IsInputAccessRequesting || !_session.Context.Grant.Allows(Permissions.ControlInput))
        {
            return;
        }

        IsInputAccessRequesting = true;
        try
        {
            await _session.RequestInputAccessAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            IsInputAccessRequesting = false;
            InputAccessMessage = $"无法请求被控端授权：{ex.Message}";
        }
    }

    // The host took something away mid-session: stop offering it. The host has already closed those channels.
    private async Task OnGrantChangedAsync()
    {
        var grant = _session.Context.Grant;
        _hostAllowsControl = grant.Allows(Permissions.ControlInput) && _input is not null;
        if (!grant.Allows(Permissions.ControlInput))
        {
            CanControl = false;
            var input = Interlocked.Exchange(ref _input, null);
            if (input is not null)
            {
                await input.DisposeAsync();
            }
        }

        if (!grant.Allows(Permissions.SecureAttention))
        {
            CanSendSecureAttention = false;
        }

        if (!grant.Allows(Permissions.FileTransfer))
        {
            CanTransferFiles = false;
            FileManagerUnavailable?.Invoke(this, EventArgs.Empty);
            await ReleaseFileManagerAsync();
        }

        if (!grant.Allows(Permissions.Camera))
        {
            CanUseCamera = false;
            ShowCameraButton = false;
            CameraUnavailable?.Invoke(this, EventArgs.Empty);
            await ReleaseCameraAsync();
        }

        if (!grant.Allows(Permissions.Terminal))
        {
            CanUseTerminal = false;
            TerminalUnavailable?.Invoke(this, EventArgs.Empty);
            await ReleaseTerminalAsync();
        }

        if (!grant.Allows(Permissions.Clipboard))
        {
            var clipboard = Interlocked.Exchange(ref _clipboard, null);
            if (clipboard is not null)
            {
                await clipboard.DisposeAsync();
            }
        }

        UpdateInputAccess();
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var mode = _session.Context.Grant.Mode == SessionMode.View ? "观看" : "控制";
        Title = $"{_session.Context.Connection.RemoteDevice.ToDisplayString()}（{mode}）";
    }

    private void UpdateDisplays()
    {
        _updatingDisplays = true;
        try
        {
            var choices = Viewer.Displays.Select(ToChoice).ToList();
            if (!choices.SequenceEqual(Displays))
            {
                Displays.Clear();
                foreach (var choice in choices)
                {
                    Displays.Add(choice);
                }
            }

            ShowDisplayPicker = Displays.Count > 1;
            SelectedDisplay = Displays.FirstOrDefault(d => d.Id == Viewer.StreamInfo?.DisplayId);
        }
        finally
        {
            _updatingDisplays = false;
        }
    }

    private static DisplayChoice ToChoice(DisplayDescription display) => new(
        display.Id,
        string.Create(CultureInfo.InvariantCulture, $"{display.Name} · {display.Width}×{display.Height}{(display.Primary ? " · 主" : string.Empty)}"));

    private void UpdateStatus()
    {
        var info = Viewer.StreamInfo;
        if (info is null)
        {
            return;
        }

        var stats = Viewer.Statistics;
        var path = _session.Path == ConnectionPath.Direct ? "直连" : "中继";
        var rtt = _session.RoundTripTime is { } value ? $" · RTT {value.TotalMilliseconds:0} ms" : string.Empty;
        var latency = stats.LatencyMilliseconds is { } ms ? $" · 画面延迟 {ms:0} ms" : string.Empty;
        var encoder = info.HardwareEncoder ? "硬件编码" : "软件编码";
        StatusText = string.Create(CultureInfo.InvariantCulture,
            $"{info.Width}×{info.Height} · {stats.FramesPerSecond:0} fps · {stats.Kbps / 1000:0.0} Mbps · {encoder}（{info.EncoderName}）· 解码 {stats.DecodeMilliseconds:0.0} ms{latency} · {path}{rtt}");
    }
}
