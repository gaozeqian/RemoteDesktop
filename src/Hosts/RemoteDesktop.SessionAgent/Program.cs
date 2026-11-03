using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Features.Camera;
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
        // One file per desktop kind: a user agent runs for the life of the session, and two agents writing one
        // file would need the shared sink — which ignores flushToDiskInterval, leaving a stuck stream with no log
        // on disk. Separate files keep the regular sink, whose periodic flush makes the encoder/capture lines
        // readable while the agent is still running.
        Path.Combine(dataDirectory, "logs", $"agent-{desktop}-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        // A long-lived agent writes little; without an interval the sink holds lines in its buffer and a stuck
        // stream leaves no log behind to diagnose. Flush every second so the encoder/capture lines are on disk.
        flushToDiskInterval: TimeSpan.FromSeconds(1),
        formatProvider: CultureInfo.InvariantCulture,
        outputTemplate: $"{{Timestamp:HH:mm:ss.fff}} [{{Level:u3}}] [agent {Environment.ProcessId}/{desktop}] {{Message:lj}}{{NewLine}}{{Exception}}")
    .CreateLogger();
builder.Logging.ClearProviders().AddSerilog(logger, dispose: true);

using var stop = new CancellationTokenSource();
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    logger.Fatal(e.ExceptionObject as Exception, "Unhandled exception in the agent.");

    // The runtime tears the process down as soon as this handler returns, and the file sink flushes only once a
    // second: the line that explains the crash would be the one still in the buffer. Disposing the logger writes it.
    logger.Dispose();
};

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
        .AddFileTransferFeature(partialFileLedgerPath: Path.Combine(dataDirectory, "partial-files.log"))
        .AddTerminalFeature()
        .AddCameraFeature()
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
