using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Audio;
using RemoteDesktop.Abstractions.Clipboard;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Media;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Client.ViewModels;

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
    private readonly DispatcherTimer _statusTimer;
    private RemoteSession? _session;
    private bool _updatingFromHost;
    private int _cameraChangesPending;

    public MainWindowViewModel(
        IHostControl hostControl,
        ControllerSessionService controller,
        VideoCodecSelector codecs,
        TimeProvider time,
        ILogger<DesktopViewer> viewerLogger,
        IAudioPlayerFactory players,
        IClipboard clipboard,
        ILoggerFactory loggers)
    {
        _hostControl = hostControl;
        _controller = controller;
        _codecs = codecs;
        _time = time;
        _viewerLogger = viewerLogger;
        _players = players;
        _clipboard = clipboard;
        _loggers = loggers;

        // The host side may live in another process (the background service), so its state is polled rather than observed.
        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _ = RefreshHostStatusAsync());
        _statusTimer.Start();
        _ = RefreshHostStatusAsync();
    }

    [ObservableProperty]
    public partial string LocalDeviceId { get; set; } = "--- --- ---";

    [ObservableProperty]
    public partial string ServerStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TemporaryPassword { get; set; } = string.Empty;

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

    /// <summary>Whether controllers may use this computer's camera. Off until the person here switches it on.</summary>
    [ObservableProperty]
    public partial bool AllowCamera { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial string RemoteDeviceId { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial string RemotePassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsWatchMode { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    /// <summary>Raised on the UI thread when a remote desktop window should be shown.</summary>
    public event EventHandler<RemoteDesktopViewModel>? RemoteDesktopOpened;

    partial void OnAllowCameraChanged(bool value)
    {
        if (!_updatingFromHost)
        {
            _ = ChangeCameraPolicyAsync(value);
        }
    }

    private async Task ChangeCameraPolicyAsync(bool allowed)
    {
        Interlocked.Increment(ref _cameraChangesPending);
        try
        {
            await _hostControl.SetCameraAllowedAsync(allowed, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException)
        {
            StatusMessage = "无法联系后台服务。";
        }
        finally
        {
            Interlocked.Decrement(ref _cameraChangesPending);
        }

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

    private bool CanConnect() => !IsConnected && DeviceId.TryParse(RemoteDeviceId, out _) && RemotePassword.Length > 0;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var target = DeviceId.Parse(RemoteDeviceId);
        var mode = IsWatchMode ? SessionMode.View : SessionMode.Control;
        StatusMessage = $"正在连接 {target.ToDisplayString()}…";
        try
        {
            var session = await _controller.ConnectAsync(target, RemotePassword, mode, cancellationToken);
            _session = session;
            session.RoundTripTimeChanged += (_, _) => Dispatcher.UIThread.Post(() => UpdateSessionStatus(session));
            session.Closed += (_, _) => Dispatcher.UIThread.Post(() => OnSessionClosed(session));
            IsConnected = true;
            UpdateSessionStatus(session);
            await OpenDesktopAsync(session, cancellationToken);
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
        }
        catch (OperationCanceledException)
        {
            StatusMessage = null;
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
            cameraConnection => new CameraWindowViewModel(cameraConnection, _codecs, _time, _loggers.CreateLogger<CameraWindowViewModel>())));
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
        if (Volatile.Read(ref _cameraChangesPending) == 0 && AllowCamera != status.CameraAllowed)
        {
            _updatingFromHost = true;
            AllowCamera = status.CameraAllowed;
            _updatingFromHost = false;
        }

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
}
