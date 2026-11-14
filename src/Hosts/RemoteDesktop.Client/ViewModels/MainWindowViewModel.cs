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

    /// <summary>How often a standalone client checks whether a host service has appeared since it started.</summary>
    private const long HostServiceProbeInterval = 5000;

    private const string HostServiceAppearedHint = "后台服务已启动；重启客户端后由它托管被控端（当前仍在本进程内）";

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
    private readonly ILoginItem? _loginItem;
    private readonly IClientPlatformPresentation _presentation;
    private readonly IHostInstallation _installation;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _presenceTimer;
    private readonly string _hostControlPipeName;
    private readonly bool _probeForHostService;
    private int _presenceInFlight;
    private long _nextHostServiceProbeAt;
    private bool _hostServiceAppeared;
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
        IClientPlatformPresentation presentation,
        IHostInstallation installation,
        IVideoRecorderFactory? recorderFactory = null,
        ILoginItem? loginItem = null,
        ICameraAccess? cameraAccess = null,
        StartupLink? startupLink = null)
    {
        _rendezvous = rendezvous;
        _hostControl = hostControl;
        _hostControlPipeName = environment.HostControlPipeName;
        _presentation = presentation;
        _installation = installation;
        HasCameraAccess = cameraAccess is not null;
        _probeForHostService = environment.ControlledEndInThisProcess && installation.StartsWithUserSession;
        _controller = controller;
        _codecs = codecs;
        _time = time;
        _viewerLogger = viewerLogger;
        _players = players;
        _clipboard = clipboard;
        _loggers = loggers;
        _logger = loggers.CreateLogger<MainWindowViewModel>();
        _recentDevicesStore = recentDevicesStore;
        // From the container: the platform layer decides whether this machine has a recorder at all — Media
        // Foundation on Windows, VideoToolbox on macOS, libavcodec on Linux. There used to be a hardcoded Windows
        // one behind an IsWindows() check, which meant recording silently did nothing on any other platform rather
        // than failing to compile.
        _recorderFactory = recorderFactory;
        // Same idea: only macOS registers a login item, and only macOS needs one — on Windows the unattended host is
        // the installed service, which the server MSI sets up. Null where the platform layer registers none, and
        // that null hides the whole UI section.
        _loginItem = loginItem;
        if (_loginItem is not null)
        {
            RefreshLoginItemState();
        }

        SelectedConnectionMode = ConnectionModes[0];
        DataDirectory = environment.DataDirectory;
        IsHostService = !environment.ControlledEndInThisProcess;
        ClientCodeNote = ClientPlatformPresentation.DescribeClientCode(environment.ControlledEndInThisProcess);
        ServerAddressNote = _presentation.DescribeServerAddress(environment.ControlledEndInThisProcess);
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

        if (startupLink?.Value is { } link)
        {
            ApplyConnectionLink(link);
        }
    }

    /// <summary>
    /// Fills in the device code a <c>remotedesktop://</c> link carried. It deliberately does not connect: the link
    /// came from a browser, a chat message or an e-mail, so the person confirms the code and supplies the password.
    /// </summary>
    public void ApplyConnectionLink(ConnectionLink link)
    {
        RemoteDeviceId = link.Device.ToDisplayString();
        StatusMessage = $"已从链接填入设备代码 {link.Device.ToDisplayString()}，输入密码后点“连接”。";
        SelectedPageIndex = 0;
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

    /// <summary>
    /// The camera grant, on a platform that needs an explicit one. Null when the host is older than this window or
    /// when the operating system asks when the camera is opened.
    /// </summary>
    [ObservableProperty]
    public partial CameraAccessState? CameraAccess { get; set; }

    [ObservableProperty]
    public partial string CameraAccessStatus { get; set; } = "状态未知";

    [ObservableProperty]
    public partial string? CameraAccessError { get; set; }

    [ObservableProperty]
    public partial bool IsCameraAccessBusy { get; set; }

    /// <summary>
    /// Whether the camera-permission section applies. It is shown exactly where the platform layer registered an
    /// <see cref="ICameraAccess"/>: the Linux portal has a separate grant to manage, the other platforms ask through
    /// their own camera APIs and have none.
    /// </summary>
    public bool HasCameraAccess { get; }

    /// <summary>
    /// Whether this machine has a login-item concept. Only the macOS layer registers <see cref="ILoginItem"/>, and
    /// only macOS needs one: on Windows the same job is done by the installed service, so the section stays hidden
    /// rather than offering a second way to do it.
    /// </summary>
    public bool HasLoginItem => _loginItem is not null;

    /// <summary>State of the login item, as macOS itself reports it (the user may also change it in System Settings).</summary>
    [ObservableProperty]
    public partial bool IsLoginItemRegistered { get; set; }

    [ObservableProperty]
    public partial string LoginItemStatus { get; set; } = "未开启";

    [ObservableProperty]
    public partial string? LoginItemError { get; set; }

    /// <summary>Where the host side runs and what its session agent is doing, when a background service hosts it.</summary>
    [ObservableProperty]
    public partial string? HostDetail { get; set; }

    /// <summary>
    /// A page-independent banner for the one case that needs the person to act: a background service appeared while
    /// this window was already hosting. <see cref="HostDetail"/> carries the same sentence on the home and settings
    /// pages, but the window may be on any tab when the service starts.
    /// </summary>
    [ObservableProperty]
    public partial string? HostServiceHint { get; set; }

    /// <summary>Whether the controlled end runs in the installed background service (as opposed to this process).</summary>
    [ObservableProperty]
    public partial bool IsHostService { get; set; }

    /// <summary>Whether the 被控端信令服务器 block can be edited: an installed service that reported its address.</summary>
    [ObservableProperty]
    public partial bool CanEditHostServerAddress { get; set; }

    /// <summary>The caption under 本机控制端代码, refreshed when the hosting mode becomes known.</summary>
    [ObservableProperty]
    public partial string ClientCodeNote { get; set; } = string.Empty;

    /// <summary>The caption under 服务器地址, refreshed when the hosting mode becomes known.</summary>
    [ObservableProperty]
    public partial string ServerAddressNote { get; set; } = string.Empty;

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

    // The same three-part number the packages and the bundle carry. AssemblyVersion would say 1.0.0.0, which is a
    // different number on the same build - see ProductVersion for why the product has one answer.
    public string ClientVersion { get; } = ProductVersion.Display;

    /// <summary>The sidebar's version line, built from <see cref="ClientVersion"/> so the two cannot disagree.</summary>
    public string ClientVersionLabel => $"Remote Desktop {ClientVersion}";

    // The captions below describe how this build actually behaves, and the three platforms differ in all of them:
    // where a fixed password is kept (DPAPI / keychain / owner-only file), what changing the server address costs
    // (elevation and a service restart / a process restart / neither), and what the controlled side even is
    // (an installed service / this process / a systemd --user service). The platform-specific words come from
    // IClientPlatformPresentation, chosen next to the platform layer, so this view model no longer asks which OS it
    // is running on.

    /// <summary>The caption under 固定密码, naming the store this platform actually uses.</summary>
    public string FixedPasswordStorageNote => _presentation.FixedPasswordStorageNote;

    /// <summary>The caption under 服务器地址. It depends on the platform and on who hosts the controlled end.</summary>
    internal string DescribeServerAddress(bool controlledEndInThisProcess) =>
        _presentation.DescribeServerAddress(controlledEndInThisProcess);

    /// <summary>
    /// Linux only: a fixed password cannot make this machine unattended there — the desktop portal refuses to
    /// persist a remote-desktop session, so every control connection needs a fresh consent at the machine. Null
    /// on the other two platforms, hidden together with <see cref="ShowFixedPasswordPlatformNote"/>.
    /// </summary>
    public string? FixedPasswordPlatformNote => _presentation.FixedPasswordPlatformNote;

    /// <summary>Whether <see cref="FixedPasswordPlatformNote"/> has anything to say on this platform.</summary>
    public bool ShowFixedPasswordPlatformNote => _presentation.ShowFixedPasswordPlatformNote;

    /// <summary>
    /// The sentence for "the host did not answer", which has to name the thing that is actually unreachable: a
    /// background service on Windows and Linux, this process's own host state on macOS.
    /// </summary>
    private string HostUnreachable => _presentation.HostUnreachable;

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


    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not open {Feature} for {Device} after connecting; the session was closed again.")]
    private static partial void LogFeatureOpenFailed(ILogger logger, Exception ex, string feature, string device);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the recent devices list.")]
    private static partial void LogDeviceListReadFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the recent devices list.")]
    private static partial void LogDeviceListWriteFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not ask the server which devices are online.")]
    private static partial void LogPresencePollFailed(ILogger logger, Exception ex);
}
