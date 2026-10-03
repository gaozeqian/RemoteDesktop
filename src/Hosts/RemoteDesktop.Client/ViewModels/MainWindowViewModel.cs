using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Media;
using RemoteDesktop.Transport;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Client.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IRendezvousClient _rendezvous;
    private readonly HostCredentials _credentials;
    private readonly ControllerSessionService _controller;
    private readonly HostSessionService _host;
    private readonly VideoCodecSelector _codecs;
    private readonly TimeProvider _time;
    private readonly ILogger<DesktopViewer> _viewerLogger;
    private RemoteSession? _session;

    public MainWindowViewModel(
        IRendezvousClient rendezvous,
        HostCredentials credentials,
        ControllerSessionService controller,
        HostSessionService host,
        VideoCodecSelector codecs,
        TimeProvider time,
        ILogger<DesktopViewer> viewerLogger)
    {
        _rendezvous = rendezvous;
        _credentials = credentials;
        _controller = controller;
        _host = host;
        _codecs = codecs;
        _time = time;
        _viewerLogger = viewerLogger;

        _rendezvous.StateChanged += (_, _) => Dispatcher.UIThread.Post(UpdateServerState);
        _credentials.Changed += (_, _) => Dispatcher.UIThread.Post(() => TemporaryPassword = _credentials.TemporaryPassword);
        _host.SessionStarted += (_, _) => Dispatcher.UIThread.Post(UpdateIncomingStatus);
        _host.SessionEnded += (_, _) => Dispatcher.UIThread.Post(UpdateIncomingStatus);

        TemporaryPassword = _credentials.TemporaryPassword;
        UpdateServerState();
    }

    [ObservableProperty]
    public partial string LocalDeviceId { get; set; } = "--- --- ---";

    [ObservableProperty]
    public partial string ServerStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TemporaryPassword { get; set; }

    /// <summary>Who is controlling this device right now, if anyone.</summary>
    [ObservableProperty]
    public partial string? IncomingStatus { get; set; }

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

    [RelayCommand]
    private void RefreshPassword() => _credentials.RotateTemporaryPassword();

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

        RemoteDesktopOpened?.Invoke(this, new RemoteDesktopViewModel(session, viewer, input));
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

    private void UpdateServerState()
    {
        LocalDeviceId = _rendezvous.DeviceId?.ToDisplayString() ?? "--- --- ---";
        ServerStatus = _rendezvous.State switch
        {
            RendezvousState.Online => "● 已连接服务器",
            RendezvousState.Connecting => "○ 正在连接服务器…",
            _ => "○ 未连接服务器",
        };
    }

    private void UpdateIncomingStatus()
    {
        var sessions = _host.ActiveSessions;
        IncomingStatus = sessions.Count == 0
            ? null
            : string.Join("\n", sessions.Select(s =>
                $"{s.Connection.RemoteDevice.ToDisplayString()} 正在{(s.Grant.Mode == SessionMode.View ? "观看" : "控制")}本机"));
    }
}
