using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Protocol.Desktop;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Client.ViewModels;

/// <summary>A display of the host that can be streamed.</summary>
public sealed record DisplayChoice(string Id, string Label);

/// <summary>One remote desktop window: the video stream, the input path, audio, clipboard and the session they belong to.</summary>
public sealed partial class RemoteDesktopViewModel : ObservableObject, IAsyncDisposable
{
    private readonly RemoteSession _session;
    private readonly AudioPlayback? _audio;
    private readonly DispatcherTimer _statusTimer;
    private InputSender? _input;
    private ClipboardShare? _clipboard;
    private FileManagerViewModel? _files;
    private TerminalWindowViewModel? _terminal;
    private CameraWindowViewModel? _camera;
    private readonly Func<IPeerConnection, CameraWindowViewModel>? _cameraFactory;
    private bool _updatingDisplays;
    private int _closed;

    public RemoteDesktopViewModel(
        RemoteSession session,
        DesktopViewer viewer,
        InputSender? input,
        AudioPlayback? audio,
        ClipboardShare? clipboard,
        Func<IPeerConnection, CameraWindowViewModel>? cameraFactory = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(viewer);

        _session = session;
        _cameraFactory = cameraFactory;
        CanUseCamera = session.Context.Grant.Allows(Permissions.Camera) && cameraFactory is not null;
        ShowCameraButton = CanUseCamera;
        Viewer = viewer;
        _input = input;
        _audio = audio;
        _clipboard = clipboard;
        CanControl = input is not null;
        CanTransferFiles = session.Context.Grant.Allows(Permissions.FileTransfer);
        CanUseTerminal = session.Context.Grant.Allows(Permissions.Terminal);
        HasAudio = audio is not null;
        UpdateTitle();

        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStatus());
        _statusTimer.Start();

        viewer.StreamInfoChanged += (_, _) => Dispatcher.UIThread.Post(UpdateDisplays);
        viewer.Closed += (_, _) => Dispatcher.UIThread.Post(() => _ = CloseAsync());
        session.Closed += (_, _) => Dispatcher.UIThread.Post(() => _ = CloseAsync());
        session.GrantChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = OnGrantChangedAsync());
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

    /// <summary>False in watch mode, and from the moment the host switches the session to watch-only: input is neither captured nor sent.</summary>
    [ObservableProperty]
    public partial bool CanControl { get; set; }

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

    // The host took something away mid-session: stop offering it. The host has already closed those channels.
    private async Task OnGrantChangedAsync()
    {
        var grant = _session.Context.Grant;
        if (!grant.Allows(Permissions.ControlInput))
        {
            CanControl = false;
            var input = Interlocked.Exchange(ref _input, null);
            if (input is not null)
            {
                await input.DisposeAsync();
            }
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
