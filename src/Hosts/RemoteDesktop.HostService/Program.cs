using System.Globalization;
using System.Security.Cryptography;
using RemoteDesktop.Application;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.HostService;
using RemoteDesktop.Features.Camera;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Features.Terminal;
using RemoteDesktop.Media;
using RemoteDesktop.Platform.Linux;
using RemoteDesktop.Platform.Windows;
using RemoteDesktop.Platform.Windows.Agent;
using RemoteDesktop.Transport;
using Serilog;

// No [assembly: SupportedOSPlatform("windows")] here on purpose. It would tell the analyzer that the whole program is
// Windows-only, which silences CA1416 everywhere — including on the calls that are genuinely Windows-only and now sit
// next to code that runs on Linux. Without it the analyzer checks each of those call sites instead.

// Composition root of the host side. What that means differs by platform, and the difference is the whole of the
// branch below:
//
//   Windows  the privileged service runs in session 0, which cannot see a desktop. Screen and input live in a session
//            agent it supervises, and this process only relays their channels.
//   elsewhere  there is no session 0 to be blinded by. A Wayland session's portals answer only inside that session,
//            so the host runs as the signed-in user inside their graphical session and is itself the thing that
//            captures and injects. Nothing is relayed and there is nobody to supervise.
if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
{
    Console.Error.WriteLine($"This build runs on Windows and Linux; this is {Environment.OSVersion.Platform}.");
    return 1;
}

// Before anything is built, so that it still answers on a machine where the service cannot start — which is the
// machine somebody runs it on. This end is where the portal, PipeWire and libei are actually reached for, so it is
// the end that has the most to say and the one most likely to be installed on its own.
if (OperatingSystem.IsLinux() &&
    args.Any(argument => string.Equals(argument, "--self-check", StringComparison.OrdinalIgnoreCase)))
{
    return LinuxSelfCheck.RunReport(Console.Out, DataDirectory.ForMachine());
}

const string ServiceName = "RemoteDesktopHost";

// The installer runs this executable for its setup and cleanup steps; the service itself is not started then.
if (OperatingSystem.IsWindows() &&
    SetupCommands.TryRun(
        args,
        AppContext.BaseDirectory,
        DataDirectory.ForMachine(),
        Console.Out,
        out var setupExitCode))
{
    return setupExitCode;
}

var builder = Host.CreateApplicationBuilder(args);
builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));

var dataDirectory = builder.Configuration["DataDirectory"] ?? DataDirectory.ForMachine();

// The same layering the client does, and the same file: on Linux the controlled end runs as the person whose
// desktop it serves, so there is one settings file between them rather than the two accounts Windows has. What the
// package shipped beside the program under /usr/lib stays the defaults; this is what the person can change.
if (OperatingSystem.IsLinux())
{
    builder.Configuration.AddJsonFile(
        Path.Combine(dataDirectory, "appsettings.json"),
        optional: true,
        reloadOnChange: false);
}

// A service has no console and no window: if the settings cannot be read, the reason goes to a file in the log folder
// an operator is already told to look at, and the service exits with its own code rather than an unexplained crash.
var transport = StartupSettings.TryReadTransport(
    () => builder.Configuration.GetSection("Transport").Get<TransportOptions>() ?? new TransportOptions(),
    Path.Combine(dataDirectory, "logs", "startup-error.log"),
    out var settingsError);
if (transport is null)
{
    Console.Error.WriteLine(settingsError);
    return StartupSettings.InvalidSettingsExitCode;
}

// A service has no console; keep a rolling log next to the identity.
builder.Logging.ClearProviders().AddSerilog(
    new LoggerConfiguration()
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
        .WriteTo.File(Path.Combine(dataDirectory, "logs", "service-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, shared: true, formatProvider: CultureInfo.InvariantCulture)
        .CreateLogger(),
    dispose: true);

// Off unless the deployment turns it on; see TelemetryOptions for what leaves the machine when it is.
var telemetry = builder.Configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>() ?? new TelemetryOptions();

if (OperatingSystem.IsWindows())
{
    var agent = builder.Configuration.GetSection("Agent").Get<AgentGatewayOptions>() ?? new AgentGatewayOptions();
    agent.DataDirectory = dataDirectory;

    builder.Services
        .AddWindowsService(options => options.ServiceName = ServiceName)
        .AddWindowsPlatform(dataDirectory, DataProtectionScope.LocalMachine)
        .AddWindowsAgentGateway(agent)
        .AddQuicTransport(transport)
        .AddRemoteDesktopApplication(telemetry)
        .AddAgentForwardedDesktop()
        .AddLocalHostControl(isService: true)
        .AddHostedService<HostControlServer>();
}
else if (OperatingSystem.IsLinux())
{
    // The features are registered directly rather than forwarded through an agent, which is what AddDesktopFeature
    // and its siblings do here — the same list the client uses when it hosts incoming sessions itself.
    builder.Services
        .AddLinuxPlatform(dataDirectory)
        .AddQuicTransport(transport)
        .AddRemoteDesktopApplication(telemetry)
        .AddRemoteDesktopMedia()
        .AddDesktopFeature()
        .AddFileTransferFeature(partialFileLedgerPath: Path.Combine(dataDirectory, "partial-files.log"))
        .AddTerminalFeature()
        .AddCameraFeature()
        .AddLocalHostControl(isService: true)
        .AddHostedService<HostControlServer>();
}

await builder.Build().RunAsync().ConfigureAwait(false);
return 0;
