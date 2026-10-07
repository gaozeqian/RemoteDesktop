using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Audio;
using RemoteDesktop.Abstractions.Clipboard;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Client.Storage;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Camera;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Features.Terminal;
using RemoteDesktop.Media;
using RemoteDesktop.Platform.Windows.Recording;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Client.ViewModels;

public enum StandaloneFeature
{
    Control,
    Watch,
    FileTransfer,
    Terminal,
    Camera,
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IHostControl _hostControl;
    private readonly ControllerSessionService _controller;
    private readonly VideoCodecSelector _codecs;
    private readonly TimeProvider _time;
    private readonly ILogger<DesktopViewer> _viewerLogger;
    private readonly IAudioPlayerFactory _players;
    private readonly IClipboard _clipboard;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly IRecentDevicesStore _recentDevicesStore;
    private readonly IVideoRecorderFactory? _recorderFactory;
    private readonly DispatcherTimer _statusTimer;
    private RemoteSession? _session;

    public MainWindowViewModel(
        IHostControl hostControl,
        ControllerSessionService controller,
        VideoCodecSelector codecs,
        TimeProvider time,
        ILogger<DesktopViewer> viewerLogger,
        IAudioPlayerFactory players,
        IClipboard clipboard,
        ILoggerFactory loggers,
        IRecentDevicesStore recentDevicesStore,
        IVideoRecorderFactory? recorderFactory = null)
    {
        _hostControl = hostControl;
        _controller = controller;
        _codecs = codecs;
        _time = time;
        _viewerLogger = viewerLogger;
        _players = players;
        _clipboard = clipboard;
        _loggers = loggers;
        _logger = loggers.CreateLogger<MainWindowViewModel>();
        _recentDevicesStore = recentDevicesStore;
        if (recorderFactory is not null)
        {
            _recorderFactory = recorderFactory;
        }
        else if (OperatingSystem.IsWindows())
        {
            _recorderFactory = new MfVideoRecorderFactory();
        }

        // The host side may live in another process (the background service), so its state is polled rather than observed.
        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _ = RefreshHostStatusAsync());
        _statusTimer.Start();
        _ = RefreshHostStatusAsync();
        _ = LoadRecentDevicesAsync();
    }

    [ObservableProperty]
    public partial string LocalDeviceId { get; set; } = "--- --- ---";

    [ObservableProperty]
    public partial string ServerStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TemporaryPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasFixedPassword { get; set; }

    [ObservableProperty]
    public partial string FixedPasswordStatus { get; set; } = "未设置";

    [ObservableProperty]
    public partial bool IsEditingFixedPassword { get; set; }

    [ObservableProperty]
    public partial string NewFixedPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? FixedPasswordError { get; set; }

    /// <summary>Where the host side runs and what its session agent is doing, when a background service hosts it.</summary>
    [ObservableProperty]
    public partial string? HostDetail { get; set; }

    /// <summary>Shown when this computer cannot reach the signaling server: without it there is no device code at all.</summary>
    [ObservableProperty]
    public partial string? ServerHint { get; set; }

    /// <summary>Who is controlling this device right now; the person at the keyboard can cut them down to watching or disconnect them.</summary>
    public ObservableCollection<HostSessionItem> IncomingSessions { get; } = [];

    [ObservableProperty]
    public partial bool HasIncoming { get; set; }

    [ObservableProperty]
    public partial string RemoteDeviceId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RemotePassword { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchControlCommand))]
    [NotifyCanExecuteChangedFor(nameof(LaunchWatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    [ObservableProperty]
    public partial bool RememberPassword { get; set; } = true;

    public ObservableCollection<RecentDeviceItemViewModel> RecentDevices { get; } = [];

    [ObservableProperty]
    public partial RecentDeviceItemViewModel? SelectedRecentDevice { get; set; }

    [ObservableProperty]
    public partial bool HasRecentDevices { get; set; }

    /// <summary>Raised on the UI thread when a remote desktop window should be shown.</summary>
    public event EventHandler<RemoteDesktopViewModel>? RemoteDesktopOpened;

    /// <summary>Raised on the UI thread when a standalone file manager window should be shown.</summary>
    public event EventHandler<(RemoteSession Session, FileManagerViewModel Files)>? StandaloneFileManagerOpened;

    /// <summary>Raised on the UI thread when a standalone terminal window should be shown.</summary>
    public event EventHandler<(RemoteSession Session, TerminalWindowViewModel Terminal)>? StandaloneTerminalOpened;

    /// <summary>Raised on the UI thread when a standalone camera window should be shown.</summary>
    public event EventHandler<(RemoteSession Session, CameraWindowViewModel Camera)>? StandaloneCameraOpened;

    partial void OnSelectedRecentDeviceChanged(RecentDeviceItemViewModel? value)
    {
        if (value is not null)
        {
            RemoteDeviceId = value.DeviceId;
            // Always replaced, including when this device remembers no password: leaving the previous device's
            // password in the box would send it to this one, and then save it under this one's code.
            RemotePassword = value.SavedPassword ?? string.Empty;
        }
    }

    public async Task LoadRecentDevicesAsync()
    {
        var entries = await _recentDevicesStore.GetAllAsync();
        RecentDevices.Clear();
        foreach (var entry in entries)
        {
            var item = new RecentDeviceItemViewModel(
                entry,
                onSaveAlias: SaveDeviceAliasAsync,
                onRemove: RemoveDeviceAsync);

            // The password lives in the OS-protected secret store, not in the entry: read it back per device so it can
            // prefill the box, and so it is never part of anything that could be synced to an account later.
            item.SavedPassword = await _recentDevicesStore.GetPasswordAsync(entry.DeviceId);
            RecentDevices.Add(item);
        }

        HasRecentDevices = RecentDevices.Count > 0;
        if (SelectedRecentDevice is null && RecentDevices.Count > 0)
        {
            SelectedRecentDevice = RecentDevices[0];
        }
    }

    /// <summary>Returns whether the alias reached the disk, so the row can be put back when it did not.</summary>
    private async Task<bool> SaveDeviceAliasAsync(RecentDeviceItemViewModel item)
    {
        try
        {
            await _recentDevicesStore.UpdateAliasAsync(item.DeviceId, item.Alias);
            return true;
        }
        catch (Exception ex)
        {
            LogDeviceListWriteFailed(_logger, ex);
            StatusMessage = "无法保存设备别名：写入设备列表失败。";
            return false;
        }
    }

    private async Task RemoveDeviceAsync(RecentDeviceItemViewModel item)
    {
        try
        {
            await _recentDevicesStore.RemoveAsync(item.DeviceId);
        }
        catch (Exception ex)
        {
            // The row is still in the list on disk, so leaving it on screen keeps the two agreeing.
            LogDeviceListWriteFailed(_logger, ex);
            StatusMessage = "无法删除设备：写入设备列表失败。";
            return;
        }

        RecentDevices.Remove(item);
        HasRecentDevices = RecentDevices.Count > 0;
        if (SelectedRecentDevice == item)
        {
            SelectedRecentDevice = RecentDevices.FirstOrDefault();
        }
    }

    [RelayCommand]
    private async Task RemoveSelectedDeviceAsync()
    {
        if (SelectedRecentDevice is { } item)
        {
            await RemoveDeviceAsync(item);
        }
    }

    [RelayCommand]
    private void OpenFixedPasswordEditor()
    {
        NewFixedPassword = string.Empty;
        FixedPasswordError = null;
        IsEditingFixedPassword = true;
    }

    [RelayCommand]
    private void CancelFixedPasswordEditor()
    {
        NewFixedPassword = string.Empty;
        FixedPasswordError = null;
        IsEditingFixedPassword = false;
    }

    [RelayCommand]
    private async Task SaveFixedPasswordAsync()
    {
        var password = NewFixedPassword?.Trim();
        if (string.IsNullOrEmpty(password) || password.Length < 6)
        {
            FixedPasswordError = "固定密码长度至少需要 6 个字符。";
            return;
        }

        FixedPasswordError = null;
        await _hostControl.SetFixedPasswordAsync(password, CancellationToken.None);
        IsEditingFixedPassword = false;
        NewFixedPassword = string.Empty;
        await RefreshHostStatusAsync();
    }

    [RelayCommand]
    private async Task ClearFixedPasswordAsync()
    {
        await _hostControl.SetFixedPasswordAsync(null, CancellationToken.None);
        IsEditingFixedPassword = false;
        NewFixedPassword = string.Empty;
        FixedPasswordError = null;
        await RefreshHostStatusAsync();
    }

    [RelayCommand]
    private async Task RefreshPasswordAsync()
    {
        try
        {
            await _hostControl.RotateTemporaryPasswordAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException)
        {
            StatusMessage = "无法联系后台服务。";
        }

        await RefreshHostStatusAsync();
    }

    /// <summary>
    /// One desktop session at a time: a second one would overwrite the session the status bar and the disconnect
    /// button track, leaving the first one open with nothing in this window pointing at it.
    /// </summary>
    private bool CanLaunchDesktop() => !IsConnected;

    // The device list passes the row the button belongs to, and the quick connect tab passes nothing so the target is
    // the code in its own box. There is deliberately no fallback to the selected row: that would quietly send the
    // typed code - and the typed password with it - to whichever device happened to be selected instead.
    [RelayCommand(CanExecute = nameof(CanLaunchDesktop))]
    private Task LaunchControlAsync(RecentDeviceItemViewModel? item) =>
        LaunchFeatureAsync(StandaloneFeature.Control, item);

    [RelayCommand(CanExecute = nameof(CanLaunchDesktop))]
    private Task LaunchWatchAsync(RecentDeviceItemViewModel? item) =>
        LaunchFeatureAsync(StandaloneFeature.Watch, item);

    [RelayCommand]
    private Task LaunchFileTransferAsync(RecentDeviceItemViewModel? item) =>
        LaunchFeatureAsync(StandaloneFeature.FileTransfer, item);

    [RelayCommand]
    private Task LaunchTerminalAsync(RecentDeviceItemViewModel? item) =>
        LaunchFeatureAsync(StandaloneFeature.Terminal, item);

    [RelayCommand]
    private Task LaunchCameraAsync(RecentDeviceItemViewModel? item) =>
        LaunchFeatureAsync(StandaloneFeature.Camera, item);

    public async Task LaunchFeatureAsync(StandaloneFeature feature, RecentDeviceItemViewModel? targetDevice = null, CancellationToken cancellationToken = default)
    {
        var deviceIdStr = targetDevice?.DeviceId ?? RemoteDeviceId;
        var password = !string.IsNullOrEmpty(RemotePassword)
            ? RemotePassword
            : targetDevice?.SavedPassword ?? string.Empty;

        if (!DeviceId.TryParse(deviceIdStr, out var target))
        {
            StatusMessage = "请输入有效的设备代码。";
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            StatusMessage = "请输入连接密码。";
            return;
        }

        var mode = feature == StandaloneFeature.Watch ? SessionMode.View : SessionMode.Control;
        var featureName = feature switch
        {
            StandaloneFeature.Control => "远程控制",
            StandaloneFeature.Watch => "观看模式",
            StandaloneFeature.FileTransfer => "文件传输",
            StandaloneFeature.Terminal => "终端",
            StandaloneFeature.Camera => "摄像头",
            _ => "远程连接",
        };

        StatusMessage = $"正在连接 {target.ToDisplayString()}（{featureName}）…";

        RemoteSession session;
        try
        {
            session = await _controller.ConnectAsync(target, password, mode, cancellationToken);
        }
        catch (PeerConnectException ex)
        {
            StatusMessage = ex.Failure switch
            {
                PeerConnectFailure.SignalingUnavailable => "未连接到服务器。",
                PeerConnectFailure.TargetOffline => "对方设备不在线。",
                PeerConnectFailure.Rejected => "对方拒绝了连接。",
                PeerConnectFailure.Timeout => "对方无响应。",
                _ => "无法建立网络连接。",
            };
            return;
        }
        catch (SessionException ex)
        {
            StatusMessage = ex.Failure switch
            {
                SessionFailure.BadPassword => "密码错误。",
                SessionFailure.TooManyAttempts => "失败次数过多，请稍后再试。",
                SessionFailure.IncompatibleVersion => "对方版本不兼容。",
                _ => "会话建立失败。",
            };
            return;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = null;
            return;
        }

        try
        {
            await RememberDeviceAsync(target, password, targetDevice, cancellationToken);

            switch (feature)
            {
                case StandaloneFeature.Control:
                case StandaloneFeature.Watch:
                    _session = session;
                    session.RoundTripTimeChanged += (_, _) => Dispatcher.UIThread.Post(() => UpdateSessionStatus(session));
                    session.Closed += (_, _) => Dispatcher.UIThread.Post(() => OnSessionClosed(session));
                    IsConnected = true;
                    UpdateSessionStatus(session);
                    await OpenDesktopAsync(session, cancellationToken);
                    break;

                case StandaloneFeature.FileTransfer:
                    if (!session.Context.Grant.Allows(Permissions.FileTransfer))
                    {
                        StatusMessage = "对方未开启文件传输权限。";
                        await session.DisposeAsync();
                        return;
                    }

                    var files = new FileManagerViewModel(session.Context.Connection);
                    await files.InitializeAsync();
                    StatusMessage = $"已连接 {target.ToDisplayString()} · 文件传输";
                    session.Closed += (_, _) => Dispatcher.UIThread.Post(() => StatusMessage = "文件传输会话已结束。");
                    StandaloneFileManagerOpened?.Invoke(this, (session, files));
                    break;

                case StandaloneFeature.Terminal:
                    if (!session.Context.Grant.Allows(Permissions.Terminal))
                    {
                        StatusMessage = "对方未开启终端权限。";
                        await session.DisposeAsync();
                        return;
                    }

                    var terminal = new TerminalWindowViewModel(session.Context.Connection);
                    await terminal.InitializeAsync();
                    StatusMessage = $"已连接 {target.ToDisplayString()} · 终端";
                    session.Closed += (_, _) => Dispatcher.UIThread.Post(() => StatusMessage = "终端会话已结束。");
                    StandaloneTerminalOpened?.Invoke(this, (session, terminal));
                    break;

                case StandaloneFeature.Camera:
                    if (!session.Context.Grant.Allows(Permissions.Camera))
                    {
                        StatusMessage = "对方未开启摄像头权限（被控端需开启允许摄像头）。";
                        await session.DisposeAsync();
                        return;
                    }

                    var camera = new CameraWindowViewModel(
                        session.Context.Connection, _codecs, _time, _loggers.CreateLogger<CameraWindowViewModel>(), _recorderFactory);
                    await camera.InitializeAsync();
                    StatusMessage = $"已连接 {target.ToDisplayString()} · 摄像头";
                    session.Closed += (_, _) => Dispatcher.UIThread.Post(() => StatusMessage = "摄像头会话已结束。");
                    StandaloneCameraOpened?.Invoke(this, (session, camera));
                    break;
            }
        }
        catch (Exception ex)
        {
            // The session is up but the feature behind it did not open (or the connect was cancelled from here on).
            // Without this the connection would stay established on the host with nothing left that could close it.
            if (ReferenceEquals(_session, session))
            {
                _session = null;
                IsConnected = false;
            }

            await CloseQuietlyAsync(session);

            if (ex is OperationCanceledException)
            {
                StatusMessage = null;
            }
            else
            {
                LogFeatureOpenFailed(_logger, ex, featureName, target.ToDisplayString());
                StatusMessage = $"已连接到 {target.ToDisplayString()}，但{featureName}未能打开。";
            }
        }
    }

    /// <summary>
    /// Records the device in the local list. This is a convenience, not part of connecting: a list that cannot be
    /// written (locked file, full disk) must not cost the session that was just established.
    /// </summary>
    private async Task RememberDeviceAsync(DeviceId target, string password, RecentDeviceItemViewModel? targetDevice, CancellationToken cancellationToken)
    {
        try
        {
            var savedPwd = RememberPassword ? password : null;
            await _recentDevicesStore.SaveOrUpdateAsync(target.ToDisplayString(), alias: targetDevice?.Alias, password: savedPwd, cancellationToken: cancellationToken);
            await LoadRecentDevicesAsync();
        }
        catch (Exception ex)
        {
            LogDeviceListWriteFailed(_logger, ex);
        }
    }

    /// <summary>Ends a session that never reached a window. A failure here must not escape onto the UI thread.</summary>
    private static async Task CloseQuietlyAsync(RemoteSession session)
    {
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception)
        {
            // The session is being abandoned either way.
        }
    }

    private async Task OpenDesktopAsync(RemoteSession session, CancellationToken cancellationToken)
    {
        var connection = session.Context.Connection;
        var viewer = await DesktopViewer.StartAsync(connection, _codecs, maxFrameRate: 60, _time, _viewerLogger, cancellationToken);

        // In watch mode the host would refuse the channel anyway; not opening it keeps the intent explicit.
        var input = session.Context.Grant.Allows(Permissions.ControlInput)
            ? await InputSender.OpenAsync(connection, cancellationToken)
            : null;

        // Sound and clipboard are extras: a device without a sound card or a locked clipboard must not cost the connection.
        var audio = session.Context.Grant.Allows(Permissions.Audio) ? await TryStartAsync(() => AudioPlayback.StartAsync(connection, _players, _loggers.CreateLogger<AudioPlayback>(), cancellationToken)) : null;
        var clipboard = session.Context.Grant.Allows(Permissions.Clipboard) ? await TryStartAsync(() => ClipboardShare.StartAsync(connection, _clipboard, _loggers, cancellationToken)) : null;

        RemoteDesktopOpened?.Invoke(this, new RemoteDesktopViewModel(
            session,
            viewer,
            input,
            audio,
            clipboard,
            _loggers.CreateLogger<RemoteDesktopViewModel>(),
            cameraConnection => new CameraWindowViewModel(cameraConnection, _codecs, _time, _loggers.CreateLogger<CameraWindowViewModel>(), _recorderFactory),
            _recorderFactory));
    }

    private static async Task<T?> TryStartAsync<T>(Func<Task<T>> start)
        where T : class
    {
        try
        {
            return await start();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private bool CanDisconnect() => IsConnected;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        if (_session is { } session)
        {
            await session.DisposeAsync();
        }
    }

    private void OnSessionClosed(RemoteSession session)
    {
        if (_session != session)
        {
            return;
        }

        _session = null;
        IsConnected = false;
        StatusMessage = "连接已断开。";
        _ = session.DisposeAsync().AsTask();
    }

    private void UpdateSessionStatus(RemoteSession session)
    {
        if (_session != session)
        {
            return;
        }

        var target = session.Context.Connection.RemoteDevice.ToDisplayString();
        var mode = session.Context.Grant.Mode == SessionMode.View ? "观看模式" : "远程控制";
        var path = session.Path == ConnectionPath.Direct ? "直连" : "中继";
        var rtt = session.RoundTripTime is { } value
            ? string.Create(CultureInfo.InvariantCulture, $" · 延迟 {value.TotalMilliseconds:0} ms")
            : string.Empty;
        StatusMessage = $"已连接 {target} · {mode} · {path}{rtt}";
    }

    private async Task RefreshHostStatusAsync()
    {
        HostStatus status;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            status = await _hostControl.GetStatusAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or OperationCanceledException)
        {
            LocalDeviceId = "--- --- ---";
            TemporaryPassword = string.Empty;
            ServerStatus = "○ 无法联系后台服务";
            SetIncoming([]);
            HostDetail = null;
            ServerHint = null;
            return;
        }

        LocalDeviceId = status.DeviceId is { } id ? new DeviceId(id).ToDisplayString() : "--- --- ---";
        TemporaryPassword = status.TemporaryPassword;
        HasFixedPassword = status.HasFixedPassword;
        FixedPasswordStatus = status.HasFixedPassword ? "已开启（长效密码）" : "未设置";
        var online = status.ServerState == nameof(Transport.Rendezvous.RendezvousState.Online);
        var where = string.IsNullOrEmpty(status.RendezvousUri) ? string.Empty : $"（{status.RendezvousUri}）";
        ServerStatus = status.ServerState switch
        {
            nameof(Transport.Rendezvous.RendezvousState.Online) => "● 已连接服务器",
            nameof(Transport.Rendezvous.RendezvousState.Connecting) => $"○ 正在连接服务器{where}…",
            _ => $"○ 未连接服务器{where}",
        };

        // The device code comes from the server, so a machine that cannot reach it has no code to give out. Saying so
        // here is the difference between "it is broken" and "the server address is wrong".
        ServerHint = online
            ? null
            : "连不上信令服务器，所以还没有设备代码。两台机器必须连同一台服务器：安装时加 RENDEZVOUSURI=ws://服务器地址:5080/ws，"
              + "或改 appsettings.json 里的 Transport:RendezvousUri 再重启服务（见 docs/deployment.md）。";
        HostDetail = status.IsService ? $"由后台服务托管{(status.AgentState is { } agent ? $" · 代理 {agent}" : string.Empty)}" : null;
        SetIncoming(status.Sessions);
    }

    // The list is rebuilt only when it really changed, so buttons do not flicker under the mouse once a second.
    private void SetIncoming(IReadOnlyList<HostSessionInfo> sessions)
    {
        if (!sessions.Select(s => (s.Id, s.Mode, s.CameraActive)).SequenceEqual(IncomingSessions.Select(s => (s.Id, s.Mode, s.CameraActive))))
        {
            IncomingSessions.Clear();
            foreach (var session in sessions)
            {
                IncomingSessions.Add(new HostSessionItem(session, _hostControl));
            }
        }

        HasIncoming = IncomingSessions.Count > 0;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not open {Feature} for {Device} after connecting; the session was closed again.")]
    private static partial void LogFeatureOpenFailed(ILogger logger, Exception ex, string feature, string device);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the recent devices list.")]
    private static partial void LogDeviceListWriteFailed(ILogger logger, Exception ex);
}
