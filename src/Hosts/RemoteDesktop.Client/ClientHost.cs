using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Application;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Application.Links;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Client.Storage;
using RemoteDesktop.Client.ViewModels;
using RemoteDesktop.Features.Camera;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Features.Terminal;
using RemoteDesktop.Media;
using RemoteDesktop.Transport;
using RemoteDesktop.Transport.Identity;

namespace RemoteDesktop.Client;

/// <summary>
/// Composition root of the desktop client. When the background service is installed it owns "being controlled" (device
/// code, password, incoming sessions) and the client is only a controller plus a window onto the service. Without it,
/// the client hosts incoming sessions itself.
/// </summary>
/// <remarks>
/// Configuration: <c>appsettings.json</c> next to the executable, overridable on the command line, e.g.
/// <c>--DataDirectory D:\rd2 --Transport:RendezvousUri ws://10.0.0.5:5080/ws</c>. A separate data directory gives a
/// separate device identity, which allows two instances on one machine. <c>--HostMode standalone</c> ignores an
/// installed service; <c>--HostControlPipe name</c> talks to a service started with another pipe name (development).
/// <para>
/// The three <see cref="SupportedOSPlatformAttribute"/> attributes are the declaration that this host runs on Windows,
/// macOS and Linux — and nothing else. They are what lets one registration call load whichever platform layer belongs
/// to the running system while keeping the platform-compatibility analyzer satisfied: the calls that matter sit behind
/// matching <c>OperatingSystem.Is…</c> checks, which narrow the set to exactly one OS.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
internal static class ClientHost
{
    /// <summary>How long a client launched by the same login waits for that service's control socket.</summary>
    private static readonly TimeSpan HostServiceStartupWait = TimeSpan.FromSeconds(3);

    /// <param name="args">The command line, with any <c>remotedesktop://</c> link already taken out.</param>
    /// <param name="startupLink">The link this process was opened with, or <c>null</c> for an ordinary launch.</param>
    public static IHost Build(string[] args, ConnectionLink? startupLink = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));

        var dataDirectory = builder.Configuration["DataDirectory"] ?? DataDirectory.ForUser();
        CrashLog.Register(dataDirectory);

        // The machine layer comes first: the installer writes the address outside the program's directory — under
        // /Library/Application Support on macOS, and under %ProgramData%\RemoteDesktop on Windows, where the MSI's
        // major upgrade would otherwise delete a live appsettings.json with the old product's files. No file, no
        // watcher: a reloading source costs an inotify instance, and desktops run out of them.
        MachineSettings.AddConfigurationSource(builder.Configuration);

        // The person's own settings file, off Windows: layered over the machine one and beneath the environment and
        // the command line, so an installed client can be repointed without editing files it does not own. On Windows
        // the elevated service owns the machine file; the client reads the same layer.
        if (!OperatingSystem.IsWindows())
        {
            UserSettings.AddConfigurationSource(builder.Configuration, dataDirectory);
        }

        // Read before anything is built: a mistyped address used to end in an exception nobody saw, because a windowed
        // program that dies while starting up has nowhere to say why. The reason goes to the crash log and to the
        // message box the entry point shows.
        var reportPath = Path.Combine(dataDirectory, "crash.log");
        var transport = StartupSettings.TryReadTransport(
            () => builder.Configuration.GetSection("Transport").Get<TransportOptions>() ?? new TransportOptions(),
            reportPath,
            out var settingsError);
        if (transport is null)
        {
            throw new StartupFailure(settingsError, reportPath);
        }

        var pipeName = builder.Configuration["HostControlPipe"] ?? HostControlProtocol.PipeName;
        var useService = !string.Equals(builder.Configuration["HostMode"], "standalone", StringComparison.OrdinalIgnoreCase) &&
                         DetectHostService(pipeName);

        builder.Services
            .AddClientPlatform(dataDirectory)
            .AddRemoteDesktopMedia()
            .AddSingleton(new ClientEnvironment(
                dataDirectory,
                ControlledEndInThisProcess: !useService,
                HostControlPipeName: pipeName))
            // No account id yet: the list belongs to this machine alone. A signed-in account passes its id here (and
            // gets its own list and remembered passwords) — see docs/design/README.md, "账号体系上线时怎么接".
            .AddSingleton<IRecentDevicesStore>(sp => new RecentDevicesStore(dataDirectory, sp.GetRequiredService<ISecretStore>()))
            // Registered even when there is no link: the window asks one question, and "no link" is an answer rather
            // than a missing service.
            .AddSingleton(new StartupLink(startupLink))
            .AddSingleton<MainWindowViewModel>();

        // Off unless the deployment turns it on. Read here rather than inside the application layer so both
        // compositions of this process — controller only, or controller and host — get the same answer.
        var telemetry = builder.Configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>() ?? new TelemetryOptions();

        if (useService)
        {
            // The service is the device others connect to; this process only makes outgoing connections.
            transport.AcceptIncoming = false;

            // This process needs its own device id. On Windows the two are already separate data directories; on Linux
            // and macOS the service runs as the signed-in user, so both would load the same secret and, because the
            // signaling server keeps one session per device, kick each other off once a second. The client's code is
            // shown as 本机控制端代码 — a different code from the service's, which is what the settings page promises.
            var identitySecretName = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
                ? DeviceIdentity.ControllerSecretName
                : DeviceIdentity.SecretName;
            builder.Services
                .AddQuicTransport(transport, identitySecretName)
                .AddRemoteDesktopApplication(telemetry)
                .AddSingleton<IHostControl>(new PipeHostControl(pipeName));
        }
        else
        {
            builder.Services
                .AddQuicTransport(transport)
                .AddRemoteDesktopApplication(telemetry)
                .AddDesktopFeature()
                .AddFileTransferFeature(partialFileLedgerPath: Path.Combine(dataDirectory, "partial-files.log"))
                .AddTerminalFeature()
                .AddCameraFeature()
                .AddLocalHostControl(isService: false);
        }

        return builder.Build();
    }

    /// <summary>
    /// Finds the background service, waiting for it when the host package says one is expected. A client started by
    /// the same login can otherwise run before the service creates its socket: it then hosts incoming sessions itself
    /// and registers under the device identity the service is about to claim, so the two abort each other every time
    /// either one reconnects.
    /// </summary>
    private static bool DetectHostService(string pipeName)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            // The control socket outlives a removed package: the user service/LaunchAgent may keep running until its
            // session ends. Without the installed unit/plist a live socket is not the controlled end the package
            // manager still shows, so this window hosts in-process rather than delegating to a removed process.
            var installed = OperatingSystem.IsMacOS()
                ? HostControlProtocol.MacOSHostInstalled
                : File.Exists(HostControlProtocol.LinuxHostServiceUnit);
            if (!installed)
            {
                return false;
            }

            // The host starts at login in the same session as the client; wait for its socket instead of racing it
            // and creating a second controlled end under the same device identity.
            return PipeHostControl.WaitUntilAvailableAsync(pipeName, HostServiceStartupWait, CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        return PipeHostControl.IsAvailableAsync(pipeName, CancellationToken.None).GetAwaiter().GetResult();
    }
}
