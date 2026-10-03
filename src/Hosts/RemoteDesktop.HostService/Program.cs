using System.Security.Cryptography;
using RemoteDesktop.Application;
using RemoteDesktop.Features.Camera;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Features.Terminal;
using RemoteDesktop.Media;
using RemoteDesktop.Platform.Windows;
using RemoteDesktop.Transport;

// Composition root of the host side: the privileged, always-on service that accepts connections,
// authorizes sessions and supervises the session agent (milestone M3).
if (!OperatingSystem.IsWindows())
{
    throw new PlatformNotSupportedException("Only Windows is supported in phase 1.");
}

// Capture, display bounds and SendInput must all use physical pixels.
WindowsProcess.EnablePerMonitorDpiAwareness();

const string ServiceName = "RemoteDesktopHost";

var builder = Host.CreateApplicationBuilder(args);
builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));

var dataDirectory = builder.Configuration["DataDirectory"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteDesktop");
var transport = builder.Configuration.GetSection("Transport").Get<TransportOptions>() ?? new TransportOptions();

builder.Services
    .AddWindowsService(options => options.ServiceName = ServiceName)
    .AddWindowsPlatform(dataDirectory, DataProtectionScope.LocalMachine)
    .AddQuicTransport(transport)
    .AddRemoteDesktopApplication()
    .AddRemoteDesktopMedia()
    .AddDesktopFeature()
    .AddFileTransferFeature()
    .AddTerminalFeature()
    .AddCameraFeature();

await builder.Build().RunAsync().ConfigureAwait(false);
