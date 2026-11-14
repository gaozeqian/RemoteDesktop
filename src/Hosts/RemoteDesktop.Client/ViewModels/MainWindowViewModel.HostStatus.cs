using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Audio;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Abstractions.Clipboard;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Application;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Application.Links;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Client.Setup;
using RemoteDesktop.Client.Storage;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Camera;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Features.Terminal;
using RemoteDesktop.Media;
using RemoteDesktop.Platform.Linux.Setup;
using RemoteDesktop.Transport;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Client.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
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
            ServerStatus = $"○ {HostUnreachable}";
            SetIncoming([]);
            HostDetail = _hostServiceAppeared ? HostServiceAppearedHint : null;
            HostServiceHint = _hostServiceAppeared ? HostServiceAppearedHint : null;
            ServerHint = null;
            HostRendezvousUri = null;
            CanEditHostServerAddress = false;
            CameraAccess = null;
            CameraAccessStatus = "状态未知";
            return;
        }

        LocalDeviceId = status.DeviceId is { } id ? new DeviceId(id).ToDisplayString() : "--- --- ---";
        TemporaryPassword = status.TemporaryPassword;
        HasFixedPassword = status.HasFixedPassword;
        FixedPasswordStatus = status.HasFixedPassword ? "已开启（长效密码）" : "未设置";
        CameraAccess = status.CameraAccess;
        CameraAccessStatus = status.CameraAccess switch
        {
            CameraAccessState.Allowed => "已允许（控制端可以使用本机摄像头）",
            CameraAccessState.Denied => "已拒绝（控制端无法使用本机摄像头）",
            CameraAccessState.Unset => "未授权 — 控制端打不开摄像头",
            CameraAccessState.Unsupported => "当前启动方式无法授权：请使用已安装的宿主服务，或从应用菜单启动客户端",
            CameraAccessState.Unknown => "状态未知（桌面权限服务没有回应）",
            _ => "状态未知",
        };
        var online = status.ServerState == nameof(Transport.Rendezvous.RendezvousState.Online);
        var where = string.IsNullOrEmpty(status.RendezvousUri) ? string.Empty : $"（{status.RendezvousUri}）";
        ServerStatus = status.ServerState switch
        {
            nameof(Transport.Rendezvous.RendezvousState.Online) => "● 已连接服务器",
            nameof(Transport.Rendezvous.RendezvousState.Connecting) => $"○ 正在连接服务器{where}…",
            _ => $"○ 未连接服务器{where}",
        };

        // The device code comes from the server, so a machine that cannot reach it has no code to give out. Saying so
        // here is the difference between "it is broken" and "the server address is wrong". The remedy named is the one
        // that platform actually has: Windows installs the address into the service at install time, macOS has no
        // service and goes through the box in 高级设置.
        ServerHint = online
            ? null
            : OperatingSystem.IsWindows()
                ? "连不上信令服务器，所以还没有设备代码。两台机器必须连同一台服务器：安装时加 RENDEZVOUSURI=wss://服务器地址:5080/ws，"
                  + "或改 appsettings.json 里的 Transport:RendezvousUri 再重启服务。"
                : "连不上信令服务器，所以还没有设备代码。两台机器必须连同一台服务器：在「高级设置 → 服务器地址」里填 "
                  + "ws://服务器地址:5080/ws 或 wss://域名/ws，应用会重启生效。";
        HostRendezvousUri = string.IsNullOrEmpty(status.RendezvousUri) ? null : status.RendezvousUri;
        // Windows with the service installed points at it. Without one — and on Linux or macOS — the controlled end
        // runs inside this process, so the row says that instead of going blank: a blank row reads like a missing
        // value. macOS adds the login-item hint it has and the other two platforms do not.
        HostDetail = status.IsService
            ? $"由后台服务托管{(status.AgentState is { } agent ? $" · 代理 {agent}" : string.Empty)}"
            : OperatingSystem.IsMacOS()
                ? "被控端在本进程内运行；开机自启请在「高级设置 → 安全」开启登录项"
                : "被控端在本进程内运行（未安装后台服务）";

        // The settings page has two sentences whose facts depend on where the controlled end runs, not on the OS: a
        // standalone Windows client has no service, so it must not claim that this window's code differs from the
        // home page's, nor promise a UAC prompt and a service restart. The same applies to a Linux standalone client.
        IsHostService = status.IsService;
        ClientCodeNote = ClientPlatformPresentation.DescribeClientCode(!status.IsService);
        ServerAddressNote = _presentation.DescribeServerAddress(!status.IsService);
        CanEditHostServerAddress = status.IsService && !string.IsNullOrEmpty(status.RendezvousUri);
        if (_loginItem is not null)
        {
            // Read every poll: the state is one property on a launchd-side object, and the user may have flipped it
            // in System Settings → 登录项 since the last tick.
            RefreshLoginItemState();
        }
        SetIncoming(status.Sessions);

        // The host package can be installed or its service started while this window is open. The composition was
        // chosen at start-up and cannot switch without restarting, so probe occasionally and say so rather than
        // leave two controlled ends registering under the same device id.
        if (_probeForHostService && !_hostServiceAppeared && Environment.TickCount64 >= _nextHostServiceProbeAt)
        {
            _nextHostServiceProbeAt = Environment.TickCount64 + HostServiceProbeInterval;

            // A socket from a package that has since been removed is not a service to hand over to. The unit file
            // (Linux) or app/launchd plist (macOS) is what tells an installed controlled end from a process the
            // package manager no longer shows.
            if (_installation.IsInstalled)
            {
                using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    _hostServiceAppeared = await PipeHostControl.IsAvailableAsync(
                        _hostControlPipeName,
                        probeTimeout.Token,
                        windowsServiceName: _installation.ServiceName);
                }
                catch (OperationCanceledException)
                {
                    // Still starting; the next probe tries again.
                }
            }
        }

        HostServiceHint = _hostServiceAppeared ? HostServiceAppearedHint : null;
        if (_hostServiceAppeared)
        {
            HostDetail = HostServiceAppearedHint;
        }
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

}
