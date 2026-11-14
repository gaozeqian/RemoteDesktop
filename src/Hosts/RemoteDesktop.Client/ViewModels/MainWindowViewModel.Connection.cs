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
                SessionFailure.TooManySessions => "对方当前连接的会话数已满，请稍后再试。",
                SessionFailure.Timeout => "对方未在限定时间内完成连接握手，请重试。",
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

                    var terminal = new TerminalWindowViewModel(session.Context.Connection, _loggers.CreateLogger<TerminalSession>());
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
            var remember = targetDevice?.RememberPassword ?? RememberPassword;
            await RecordConnectedDeviceAsync(_recentDevicesStore, target.ToDisplayString(), targetDevice?.Alias, password, remember, cancellationToken);
            await LoadRecentDevicesAsync();
        }
        catch (Exception ex)
        {
            LogDeviceListWriteFailed(_logger, ex);
        }
    }

    /// <summary>
    /// Saves the device and applies the "remember password" choice to what is stored. <c>SaveOrUpdateAsync</c> reads a
    /// null password as "leave it alone", so unticking the box has to clear the stored password explicitly or it would
    /// be read back and prefilled on the next load.
    /// </summary>
    internal static async Task RecordConnectedDeviceAsync(
        IRecentDevicesStore store,
        string deviceId,
        string? alias,
        string password,
        bool rememberPassword,
        CancellationToken cancellationToken)
    {
        await store.SaveOrUpdateAsync(deviceId, alias: alias, password: rememberPassword ? password : null, cancellationToken: cancellationToken);
        if (!rememberPassword)
        {
            await store.UpdatePasswordAsync(deviceId, null, cancellationToken);
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

}
