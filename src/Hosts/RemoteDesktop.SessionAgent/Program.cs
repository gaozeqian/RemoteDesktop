using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Features.Terminal;
using RemoteDesktop.Media;
using RemoteDesktop.Platform.Windows;
using RemoteDesktop.Platform.Windows.Agent;
using RemoteDesktop.Platform.Windows.Input;
using RemoteDesktop.SessionAgent;
using Serilog;

[assembly: SupportedOSPlatform("windows")]

// Started by the host service inside the interactive session (or on the secure desktop) to capture the screen and
// inject input, because the service itself, in Session 0, cannot. It serves the channels the service hands it over a
// named pipe, and exits when that pipe closes.
if (!OperatingSystem.IsWindows())
{
    return 1;
}

// Capture, display bounds and SendInput must all use physical pixels.
WindowsProcess.EnablePerMonitorDpiAwareness();

var builder = Host.CreateApplicationBuilder(args);
var pipeName = builder.Configuration["agent-pipe"];
if (string.IsNullOrEmpty(pipeName))
{
    return 2;
}

var dataDirectory = builder.Configuration["data-directory"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteDesktop");
var desktop = builder.Configuration["desktop"] ?? "user";

var logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(
        Path.Combine(dataDirectory, "logs", "agent-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        shared: true,
        formatProvider: CultureInfo.InvariantCulture,
        outputTemplate: $"{{Timestamp:HH:mm:ss.fff}} [{{Level:u3}}] [agent {Environment.ProcessId}/{desktop}] {{Message:lj}}{{NewLine}}{{Exception}}")
    .CreateLogger();
builder.Logging.ClearProviders().AddSerilog(logger, dispose: true);

using var stop = new CancellationTokenSource();
AppDomain.CurrentDomain.UnhandledException += (_, e) => logger.Fatal(e.ExceptionObject as Exception, "Unhandled exception in the agent.");

AgentLink link;
try
{
    link = await AgentLink.ConnectAsync(pipeName, stop.Token).ConfigureAwait(false);
}
catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
{
    logger.Error(ex, "Could not connect to the host service.");
    return 3;
}

await using (link.ConfigureAwait(false))
{
    builder.Services
        .AddSingleton(TimeProvider.System)
        .AddWindowsPlatform(dataDirectory, DataProtectionScope.CurrentUser)
        .AddRemoteDesktopMedia()
        .AddDesktopFeature()
        .AddFileTransferFeature()
        .AddTerminalFeature()
        .AddSingleton(link)
        // Last registration wins: Ctrl+Alt+Del goes through the service instead of SendInput.
        .AddSingleton<IInputInjector>(sp => new AgentInputInjector(
            new SendInputInjector(sp.GetRequiredService<IDisplayEnumerator>()),
            sp.GetRequiredService<AgentLink>()))
        .AddSingleton<AgentRunner>();

    using var host = builder.Build();
    logger.Information("Agent for the {Desktop} desktop connected to the host service.", desktop);
    await host.Services.GetRequiredService<AgentRunner>().RunAsync(stop.Token).ConfigureAwait(false);
    logger.Information("Agent exiting.");
}

return 0;
