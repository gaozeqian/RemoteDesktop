using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using RemoteDesktop.Platform.Windows;

// Launched by the host service inside the interactive session (or on the secure desktop) to capture the screen
// and inject input, because the service in Session 0 cannot. Talks to the service over a named pipe (milestone M3).
var builder = Host.CreateApplicationBuilder(args);

if (!OperatingSystem.IsWindows())
{
    throw new PlatformNotSupportedException("Only Windows is supported in phase 1.");
}

// Capture, display bounds and SendInput must all use physical pixels.
WindowsProcess.EnablePerMonitorDpiAwareness();

var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteDesktop");
builder.Services.AddWindowsPlatform(dataDirectory, DataProtectionScope.LocalMachine);

await builder.Build().RunAsync().ConfigureAwait(false);
