using System.Runtime.Versioning;
using System.Security.Cryptography;
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
using RemoteDesktop.Platform.Windows;
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
/// </remarks>
[SupportedOSPlatform("windows")]
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
        var transport = builder.Configuration.GetSection("Transport").Get<TransportOptions>() ?? new TransportOptions();

        var pipeName = builder.Configuration["HostControlPipe"] ?? HostControlProtocol.PipeName;
        var useService = !string.Equals(builder.Configuration["HostMode"], "standalone", StringComparison.OrdinalIgnoreCase) &&
                         PipeHostControl.IsAvailableAsync(pipeName, CancellationToken.None).GetAwaiter().GetResult();

        builder.Services
            .AddWindowsPlatform(dataDirectory, DataProtectionScope.CurrentUser)
            .AddRemoteDesktopMedia()
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
                .AddFileTransferFeature()
                .AddTerminalFeature()
                .AddCameraFeature()
                .AddLocalHostControl(isService: false);
        }

        return builder.Build();
    }
}
