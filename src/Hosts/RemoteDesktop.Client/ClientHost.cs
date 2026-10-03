using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemoteDesktop.Application;
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
/// Composition root of the desktop client. Until the host service takes over in milestone M3, the client also
/// hosts incoming sessions in-process.
/// </summary>
/// <remarks>
/// Configuration: <c>appsettings.json</c> next to the executable, overridable on the command line, e.g.
/// <c>--DataDirectory D:\rd2 --Transport:RendezvousUri ws://10.0.0.5:5080/ws</c>. A separate data directory gives a
/// separate device identity, which allows two instances on one machine.
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
        var transport = builder.Configuration.GetSection("Transport").Get<TransportOptions>() ?? new TransportOptions();

        builder.Services
            .AddWindowsPlatform(dataDirectory, DataProtectionScope.CurrentUser)
            .AddQuicTransport(transport)
            .AddRemoteDesktopApplication()
            .AddRemoteDesktopMedia()
            .AddDesktopFeature()
            .AddFileTransferFeature()
            .AddTerminalFeature()
            .AddCameraFeature()
            .AddSingleton<MainWindowViewModel>();

        return builder.Build();
    }
}
