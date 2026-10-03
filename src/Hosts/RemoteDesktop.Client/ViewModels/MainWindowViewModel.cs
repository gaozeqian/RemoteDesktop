using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Transport;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Client.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IRendezvousClient _rendezvous;
    private readonly HostCredentials _credentials;
    private readonly ControllerSessionService _controller;
    private readonly HostSessionService _host;
    private RemoteSession? _session;

    public MainWindowViewModel(
        IRendezvousClient rendezvous,
        HostCredentials credentials,
        ControllerSessionService controller,
        HostSessionService host)
    {
        _rendezvous = rendezvous;
        _credentials = credentials;
        _controller = controller;
        _host = host;

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
