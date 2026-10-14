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
using RemoteDesktop.Client.Setup;
using RemoteDesktop.Client.Storage;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Camera;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Features.Terminal;
using RemoteDesktop.Media;
using RemoteDesktop.Platform.Windows.Recording;
using RemoteDesktop.Platform.Windows.Setup;
using RemoteDesktop.Transport;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Client.ViewModels;

public enum StandaloneFeature
{
    Control,
    Watch,
    FileTransfer,
    Terminal,
    Camera,
}

/// <summary>One entry of the connection-mode chooser on the home page.</summary>
public sealed record ConnectionMode(string Label, StandaloneFeature Feature);

public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <summary>How often the remembered devices are asked about. Well inside the server's per-device budget.</summary>
    private static readonly TimeSpan PresenceInterval = TimeSpan.FromSeconds(30);

    /// <summary>Ids per query, so a long list stays one or two messages rather than one big one.</summary>
    private const int PresenceChunkSize = 32;

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
    private readonly IRendezvousClient _rendezvous;
    private readonly IVideoRecorderFactory? _recorderFactory;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _presenceTimer;
    private int _presenceInFlight;
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
        IRendezvousClient rendezvous,
        ClientEnvironment environment,
        TransportOptions transport,
        IVideoRecorderFactory? recorderFactory = null)
    {
        _rendezvous = rendezvous;
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
        // Straight from the platform layer, which registers one on both platforms — Media Foundation on Windows,
        // libavcodec here. There used to be a hardcoded Windows one behind an IsWindows() check, which meant
        // recording silently did nothing on any other platform rather than failing to compile.
        _recorderFactory = recorderFactory;

        SelectedConnectionMode = ConnectionModes[0];
        DataDirectory = environment.DataDirectory;
        ClientRendezvousUri = transport.RendezvousUri?.ToString() is { Length: > 0 } uri ? uri : "未配置";

        // The host side may live in another process (the background service), so its state is polled rather than observed.
        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _ = RefreshHostStatusAsync());
        _statusTimer.Start();

        // Deliberately not the one-second host timer: asking 60 times a minute is six times the server's budget per
        // device, and the server would throttle the client into a list that is permanently "unknown".
        _presenceTimer = new DispatcherTimer(PresenceInterval, DispatcherPriority.Background, (_, _) => _ = RefreshPresenceAsync());
        _presenceTimer.Start();
        UpdateClientDeviceId();
        _rendezvous.StateChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            // The code the server gave this window arrives with the registration, so it changes exactly when the
            // connection state does.
            UpdateClientDeviceId();
            if (_rendezvous.State == RendezvousState.Online)
            {
                // A server that has just come back may be a newer build than the one that did not answer.
                IsPresenceUnavailable = false;
                _ = RefreshPresenceAsync();
            }
        });

        _ = RefreshHostStatusAsync();
        _ = LoadRecentDevicesAsync();
    }

    [ObservableProperty]
    public partial string LocalDeviceId { get; set; } = "--- --- ---";

    /// <summary>
    /// The code this window registers under when it connects out to somebody else — which is not the code on the home
    /// page. Those are two identities: the background service owns "being controlled" and shows its code, while the
    /// window has one of its own for the connections it makes. A device that is being controlled lists *this* code
    /// beside the session, so it is the one to compare against when somebody asks who is connected to their machine.
    /// </summary>
    [ObservableProperty]
    public partial string ClientDeviceId { get; set; } = "未连接服务器";

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
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial bool IsConnected { get; set; }

    /// <summary>Where this client keeps its state, shown on the 高级设置 page.</summary>
    public string DataDirectory { get; }

    /// <summary>The address the host side is actually registered with, when a background service owns it.</summary>
    [ObservableProperty]
    public partial string? HostRendezvousUri { get; set; }

    [ObservableProperty]
    public partial bool IsEditingServerAddress { get; set; }

    [ObservableProperty]
    public partial string? NewServerAddress { get; set; }

    [ObservableProperty]
    public partial string? ServerAddressError { get; set; }

    public string ClientVersion { get; } = typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString() ?? "未知";

    /// <summary>The sidebar's version line, built from <see cref="ClientVersion"/> so the two cannot disagree.</summary>
    public string ClientVersionLabel => $"Remote Desktop {ClientVersion}";

    /// <summary>The address <em>this</em> process dials. In service mode that is not the address in use: the service
    /// has its own, and reports it through the control pipe as <see cref="HostRendezvousUri"/>.</summary>
    public string ClientRendezvousUri { get; }

    /// <summary>Whether the rail is showing the home page. Drives which page is realised, so the other two are not
    /// in the automation tree at all.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomePage))]
    [NotifyPropertyChangedFor(nameof(IsDeviceListPage))]
    [NotifyPropertyChangedFor(nameof(IsSettingsPage))]
    public partial int SelectedPageIndex { get; set; }

    public bool IsHomePage => SelectedPageIndex == 0;

    public bool IsDeviceListPage => SelectedPageIndex == 1;

    public bool IsSettingsPage => SelectedPageIndex == 2;

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    [ObservableProperty]
    public partial bool RememberPassword { get; set; } = true;

    public ObservableCollection<RecentDeviceItemViewModel> RecentDevices { get; } = [];

    [ObservableProperty]
    public partial RecentDeviceItemViewModel? SelectedRecentDevice { get; set; }

    [ObservableProperty]
    public partial bool HasRecentDevices { get; set; }

    /// <summary>The server did not answer the last presence query, so every dot is showing "unknown".</summary>
    [ObservableProperty]
    public partial bool IsPresenceUnavailable { get; set; }

    /// <summary>Raised on the UI thread when a remote desktop window should be shown.</summary>
    public event EventHandler<RemoteDesktopViewModel>? RemoteDesktopOpened;

    /// <summary>Raised on the UI thread when a standalone file manager window should be shown.</summary>
    public event EventHandler<(RemoteSession Session, FileManagerViewModel Files)>? StandaloneFileManagerOpened;

    /// <summary>Raised on the UI thread when a standalone terminal window should be shown.</summary>
    public event EventHandler<(RemoteSession Session, TerminalWindowViewModel Terminal)>? StandaloneTerminalOpened;

    /// <summary>Raised on the UI thread when a standalone camera window should be shown.</summary>
    public event EventHandler<(RemoteSession Session, CameraWindowViewModel Camera)>? StandaloneCameraOpened;

    /// <summary>
    /// Rebuilds the list from disk. Anything the user typed into a row in this session is carried over: the rows are
    /// recreated on every connect, and without this the detail panel would appear to forget a password that was just
    /// entered, and the selection would point at an object no longer in the list.
    /// </summary>
    public async Task LoadRecentDevicesAsync()
    {
        var entries = await _recentDevicesStore.GetAllAsync();

        var edited = RecentDevices.ToDictionary(item => item.DeviceId, StringComparer.Ordinal);
        var selectedId = SelectedRecentDevice?.DeviceId;

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
            if (edited.TryGetValue(entry.DeviceId, out var previous))
            {
                item.Password = previous.Password;
                item.RememberPassword = previous.RememberPassword;
                item.Presence = previous.Presence;
            }
            else
            {
                item.Password = item.SavedPassword ?? string.Empty;
            }

            RecentDevices.Add(item);
        }

        HasRecentDevices = RecentDevices.Count > 0;
        SelectedRecentDevice = RecentDevices.FirstOrDefault(item => string.Equals(item.DeviceId, selectedId, StringComparison.Ordinal))
            ?? RecentDevices.FirstOrDefault();

        _ = RefreshPresenceAsync();
    }

    /// <summary>Re-reads the list from disk, for the refresh button next to it.</summary>
    [RelayCommand]
    private Task RefreshRecentDevicesAsync() => LoadRecentDevicesAsync();

    /// <summary>
    /// Asks the server which remembered devices are online, and paints the answer onto the rows.
    /// <para>
    /// Every failure mode leaves the affected rows <see cref="DevicePresenceState.Unknown"/> rather than offline: an
    /// older server ignores the message entirely, a throttled one says nothing, and the client cannot tell those from
    /// a device that really is off. Only a sentence from the server is allowed to turn a dot grey.
    /// </para>
    /// </summary>
    private async Task RefreshPresenceAsync()
    {
        if (Interlocked.Exchange(ref _presenceInFlight, 1) != 0)
        {
            return;
        }

        try
        {
            if (_rendezvous.State != RendezvousState.Online)
            {
                return;
            }

            var rows = new List<(RecentDeviceItemViewModel Item, DeviceId Id)>();
            foreach (var item in RecentDevices)
            {
                if (DeviceId.TryParse(item.DeviceId, out var id))
                {
                    rows.Add((item, id));
                }
            }

            if (rows.Count == 0)
            {
                return;
            }

            var unanswered = false;
            foreach (var chunk in rows.Chunk(PresenceChunkSize))
            {
                var online = await _rendezvous.QueryPresenceAsync([.. chunk.Select(pair => pair.Id)], CancellationToken.None);
                if (online is null)
                {
                    unanswered = true;
                    foreach (var (item, _) in chunk)
                    {
                        item.Presence = DevicePresenceState.Unknown;
                    }

                    continue;
                }

                foreach (var (item, id) in chunk)
                {
                    item.Presence = online.Contains(id) ? DevicePresenceState.Online : DevicePresenceState.Offline;
                }
            }

            IsPresenceUnavailable = unanswered;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or PeerConnectException)
        {
            // A timer callback that throws has nowhere to report it, and this client keeps no log file.
            LogPresencePollFailed(_logger, ex);
        }
        finally
        {
            Interlocked.Exchange(ref _presenceInFlight, 0);
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

    /// <summary>
    /// The five ways to reach a device, chosen on the home page before dialling a typed code.
    /// </summary>
    public IReadOnlyList<ConnectionMode> ConnectionModes { get; } =
    [
        new("远程控制", StandaloneFeature.Control),
        new("观看模式", StandaloneFeature.Watch),
        new("文件传输", StandaloneFeature.FileTransfer),
        new("终端", StandaloneFeature.Terminal),
        new("摄像头", StandaloneFeature.Camera),
    ];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial ConnectionMode SelectedConnectionMode { get; set; }

    /// <summary>Mirrors the per-feature rules: only a desktop session is blocked while one is already open.</summary>
    private bool CanConnect() => SelectedConnectionMode.Feature switch
    {
        StandaloneFeature.Control or StandaloneFeature.Watch => CanLaunchDesktop(),
        _ => true,
    };

    /// <summary>
    /// Dials the code typed on the home page. No device is passed, so the target is that box alone — the rule the
    /// five per-device commands below rely on to mean the opposite.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectAsync() => LaunchFeatureAsync(SelectedConnectionMode.Feature, targetDevice: null, CancellationToken.None);

    [RelayCommand]
    private void OpenServerAddressEditor()
    {
        NewServerAddress = HostRendezvousUri;
        ServerAddressError = null;
        IsEditingServerAddress = true;
    }

    [RelayCommand]
    private void CancelServerAddressEditor()
    {
        NewServerAddress = string.Empty;
        ServerAddressError = null;
        IsEditingServerAddress = false;
    }

    [RelayCommand]
    private async Task SaveServerAddressAsync()
    {
        // There is no installed service to reconfigure anywhere but Windows. The guard is also what tells the platform
        // analyzer that the Windows-only parsing below is reached on Windows only.
        if (!OperatingSystem.IsWindows())
        {
            ServerAddressError = "只有 Windows 版本可以修改被控端的服务器地址";
            return;
        }

        // Parsed here, before anything is started as administrator, and by the same rule the service applies when it
        // writes the setting — so a rejected address costs the user a sentence, not a UAC prompt.
        if (!SystemSetup.TryParseRendezvousUri(NewServerAddress, out var rendezvous))
        {
            ServerAddressError = "地址要写成 ws://主机:端口/ws 或 wss://主机/ws";
            return;
        }

        try
        {
            var exePath = System.IO.Path.Combine(AppContext.BaseDirectory, "RemoteDesktop.HostService.exe");
            if (!System.IO.File.Exists(exePath))
            {
                ServerAddressError = "找不到后台服务程序，可能客户端未作为服务安装。";
                return;
            }

            // The service executable is asked directly, as administrator, to write the address and restart itself.
            var startInfo = HostServiceSetup.BuildStartInfo(exePath, rendezvous);

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process != null)
            {
                await process.WaitForExitAsync();
                if (process.ExitCode != 0)
                {
                    ServerAddressError = "由于权限拒绝或配置失败，无法修改信令服务器地址。";
                    return;
                }
            }

            IsEditingServerAddress = false;

            // Restart the client application so it picks up the new address for outbound connections.
            var clientExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(clientExe))
            {
                System.Diagnostics.Process.Start(clientExe);
                Environment.Exit(0);
            }
        }
        catch (Exception ex)
        {
            ServerAddressError = $"无法应用配置：{ex.Message}";
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

    // The device list passes the row the button belongs to, and the home page passes nothing so the target is the
    // code in its own box. There is deliberately no fallback to the selected row: that would quietly send the
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
        // The code and the password are resolved the same way: a row's own fields, or the typed ones. Neither falls
        // back to the other, so a click on one device's button can never spend another device's password.
        var deviceIdStr = targetDevice?.DeviceId ?? RemoteDeviceId;
        var password = targetDevice is not null ? targetDevice.Password : RemotePassword;

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
            if (ex.Failure == PeerConnectFailure.TargetOffline && targetDevice is not null)
            {
                // The server just said so outright: this row is offline, no need to wait for the next poll.
                targetDevice.Presence = DevicePresenceState.Offline;
            }

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
            var savedPwd = (targetDevice?.RememberPassword ?? RememberPassword) ? password : null;
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
            HostRendezvousUri = null;
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
        HostRendezvousUri = string.IsNullOrEmpty(status.RendezvousUri) ? null : status.RendezvousUri;
        HostDetail = status.IsService ? $"由后台服务托管{(status.AgentState is { } agent ? $" · 代理 {agent}" : string.Empty)}" : null;
        SetIncoming(status.Sessions);
    }

    private void UpdateClientDeviceId() =>
        ClientDeviceId = _rendezvous.DeviceId is { } id ? id.ToDisplayString() : "未连接服务器";

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not ask the server which devices are online.")]
    private static partial void LogPresencePollFailed(ILogger logger, Exception ex);
}
