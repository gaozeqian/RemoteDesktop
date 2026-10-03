using System.Security.Cryptography;
using RemoteDesktop.Application;
using RemoteDesktop.Features.Camera;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Features.Terminal;
using RemoteDesktop.HostService;
using RemoteDesktop.Media;
using RemoteDesktop.Platform.Windows;

// Composition root of the host side: the privileged, always-on service that accepts connections,
// authorizes sessions and supervises the session agent.
var builder = Host.CreateApplicationBuilder(args);
builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));

builder.Services
    .AddRemoteDesktopApplication()
    .AddRemoteDesktopMedia()
    .AddDesktopFeature()
    .AddFileTransferFeature()
    .AddTerminalFeature()
    .AddCameraFeature();

if (OperatingSystem.IsWindows())
{
    builder.Services.AddWindowsService(options => options.ServiceName = HostWorker.ServiceName);
    var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteDesktop");
    builder.Services.AddWindowsPlatform(dataDirectory, DataProtectionScope.LocalMachine);
}
else
{
    throw new PlatformNotSupportedException("Only Windows is supported in phase 1.");
}

builder.Services.AddHostedService<HostWorker>();

await builder.Build().RunAsync().ConfigureAwait(false);
