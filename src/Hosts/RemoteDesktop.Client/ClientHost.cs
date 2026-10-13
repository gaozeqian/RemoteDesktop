using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Application;
using RemoteDesktop.Application.Hosting;
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
/// The two <see cref="SupportedOSPlatformAttribute"/> attributes are the declaration that this host runs on Windows
/// and on macOS — and nothing else. They are what lets one method register either platform layer while keeping the
/// platform-compatibility analyzer satisfied: each call below sits inside a matching <c>OperatingSystem.Is…</c> check,
/// which narrows that set to exactly one OS.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("macos")]
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

        var dataDirectory = builder.Configuration["DataDirectory"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteDesktop");
        CrashLog.Register(dataDirectory);
        AddSavedSettings(builder.Configuration, dataDirectory);

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
            .AddSingleton(new ClientEnvironment(dataDirectory))
            // No account id yet: the list belongs to this machine alone. A signed-in account passes its id here (and
            // gets its own list and remembered passwords) — see docs/device-list-storage.md.
            .AddSingleton<IRecentDevicesStore>(sp => new RecentDevicesStore(dataDirectory, sp.GetRequiredService<ISecretStore>()))
            .AddSingleton<MainWindowViewModel>();

        if (useService)
        {
            // The service is the device others connect to; this process only makes outgoing connections.
            transport.AcceptIncoming = false;
            builder.Services
                .AddQuicTransport(transport)
                .AddRemoteDesktopApplication()
                .AddSingleton<IHostControl>(new PipeHostControl(pipeName));
        }
        else
        {
            builder.Services
                .AddQuicTransport(transport)
                .AddRemoteDesktopApplication()
                .AddDesktopFeature()
                .AddFileTransferFeature(partialFileLedgerPath: Path.Combine(dataDirectory, "partial-files.log"))
                .AddTerminalFeature()
                .AddCameraFeature()
                .AddLocalHostControl(isService: false);
        }

        return builder.Build();
    }

    /// <summary>
    /// Loads the address the user saved in 高级设置 over the one the app shipped with.
    /// </summary>
    /// <remarks>
    /// The saved value lives in the data directory rather than in the <c>appsettings.json</c> next to the executable,
    /// because on macOS that file is inside the signed .app and rewriting it would invalidate the signature — which
    /// there also costs the screen-recording and accessibility grants. See <see cref="ServerAddressStore"/>.
    /// <para>
    /// The source is inserted directly above the bundled appsettings.json rather than simply appended. Appending would
    /// put it above the environment and the command line too, and then <c>--Transport:RendezvousUri</c> would stop
    /// being an override — it would look like it was ignored, which is exactly the kind of thing that costs an hour.
    /// </para>
    /// </remarks>
    private static void AddSavedSettings(ConfigurationManager configuration, string dataDirectory)
    {
        var path = ServerAddressStore.PathFor(dataDirectory);
        if (!File.Exists(path))
        {
            return;
        }

        // AddJsonFile is what wires up the file provider for an absolute path; the source then has to move.
        configuration.AddJsonFile(path, optional: true, reloadOnChange: false);
        var sources = configuration.Sources;
        var saved = sources[^1];
        sources.RemoveAt(sources.Count - 1);

        var afterAppSettings = 0;
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is FileConfigurationSource file
                && string.Equals(Path.GetFileName(file.Path), "appsettings.json", StringComparison.OrdinalIgnoreCase))
            {
                afterAppSettings = i + 1;
            }
        }

        sources.Insert(afterAppSettings, saved);
    }
}
