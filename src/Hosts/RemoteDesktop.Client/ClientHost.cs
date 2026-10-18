using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Application;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Client.Storage;
using RemoteDesktop.Client.ViewModels;
using RemoteDesktop.Features.Camera;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Features.Terminal;
using RemoteDesktop.Media;
using RemoteDesktop.Transport;

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
    public static IHost Build(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));

        var dataDirectory = builder.Configuration["DataDirectory"] ?? DataDirectory.ForUser();
        CrashLog.Register(dataDirectory);

        // The person's own settings file, off Windows: layered over the shipped one and beneath the environment and
        // the command line, so an installed client can be repointed without editing files it does not own. On Windows
        // the installed service owns the address in its own appsettings.json instead — see UserSettings for the whole
        // story. No file, no watcher: a reloading source costs an inotify instance, and desktops run out of them.
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
                         PipeHostControl.IsAvailableAsync(pipeName, CancellationToken.None).GetAwaiter().GetResult();

        builder.Services
            .AddClientPlatform(dataDirectory)
            .AddRemoteDesktopMedia()
            .AddSingleton(new ClientEnvironment(dataDirectory, ControlledEndInThisProcess: !useService))
            // No account id yet: the list belongs to this machine alone. A signed-in account passes its id here (and
            // gets its own list and remembered passwords) — see docs/ops/README.md.
            .AddSingleton<IRecentDevicesStore>(sp => new RecentDevicesStore(dataDirectory, sp.GetRequiredService<ISecretStore>()))
            .AddSingleton<MainWindowViewModel>();

        // Off unless the deployment turns it on. Read here rather than inside the application layer so both
        // compositions of this process — controller only, or controller and host — get the same answer.
        var telemetry = builder.Configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>() ?? new TelemetryOptions();

        if (useService)
        {
            // The service is the device others connect to; this process only makes outgoing connections.
            transport.AcceptIncoming = false;
            builder.Services
                .AddQuicTransport(transport)
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
}
