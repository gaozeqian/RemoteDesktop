using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using RemoteDesktop.Application;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Hosts.Composition;
using RemoteDesktop.HostService;
using RemoteDesktop.Media;
using RemoteDesktop.Platform.Linux;
using RemoteDesktop.Platform.MacOS;
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
if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
{
    Console.Error.WriteLine($"This build runs on Windows, Linux and macOS; this is {Environment.OSVersion.Platform}.");
    return 1;
}

// Before the service starts, and before any settings are read: an installer has to be able to set the fixed password
// on a machine where nobody is going to open a window. On the Unixes the same command is how an operator does it by
// hand, as the user the host runs as.
var credentialDataDirectory = DataDirectory.ForMachine();
var dataDirectoryArgument = Array.IndexOf(args, "--DataDirectory");
if (dataDirectoryArgument >= 0 && dataDirectoryArgument + 1 < args.Length && !string.IsNullOrWhiteSpace(args[dataDirectoryArgument + 1]))
{
    credentialDataDirectory = args[dataDirectoryArgument + 1];
}

if (HostCredentialCommands.TryRun(args, credentialDataDirectory, Console.Out, out var credentialExitCode))
{
    return credentialExitCode;
}

// The camera grant has to be made by the running service: the portal identifies the application by the systemd user
// unit it runs under, and a one-shot process started from a shell has a different cgroup and therefore no app id the
// portal knows. This command asks the service to make the grant, which is also how a host with no graphical client
// installed gets its camera allowed.
var cameraAccessArgument = Array.IndexOf(args, "--camera-access");
if (OperatingSystem.IsLinux() && cameraAccessArgument >= 0)
{
    var value = cameraAccessArgument + 1 < args.Length ? args[cameraAccessArgument + 1] : null;
    var allowed = string.Equals(value, "allow", StringComparison.OrdinalIgnoreCase);
    var refused = string.Equals(value, "deny", StringComparison.OrdinalIgnoreCase);
    if (!allowed && !refused)
    {
        Console.Error.WriteLine("Usage: RemoteDesktop.HostService --camera-access allow|deny [--HostControlPipe <name>]");
        return 2;
    }

    var pipeName = HostControlProtocol.PipeName;
    var pipeArgument = Array.IndexOf(args, "--HostControlPipe");
    if (pipeArgument >= 0 && pipeArgument + 1 < args.Length && !string.IsNullOrWhiteSpace(args[pipeArgument + 1]))
    {
        pipeName = args[pipeArgument + 1];
    }

    try
    {
        await new PipeHostControl(pipeName).SetCameraAccessAsync(allowed, CancellationToken.None);
        Console.WriteLine(allowed
            ? "Camera access granted. The camera portal will answer this host without asking again."
            : "Camera access revoked. The next controller will be told the host does not allow its camera.");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
    {
        Console.Error.WriteLine($"Could not change camera access: {ex.Message}");
        return 1;
    }
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
        DataDirectory.ForMachine(),
        Console.Out,
        out var setupExitCode))
{
    return setupExitCode;
}

// The content root matters when launchd starts this executable straight out of the bundle: the working directory is
// not Contents/MacOS, so without it the appsettings.json next to the program is never read. The same setting is what
// the client uses. It has to be given at construction, before the file sources are layered on top.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));

var dataDirectory = builder.Configuration["DataDirectory"] ?? DataDirectory.ForMachine();

// The machine-wide file the installer writes sits between the shipped defaults and the person's own file. On macOS it
// is /Library/Application Support/RemoteDesktop because the shipped file is inside the signed bundle; on Windows it is
// %ProgramData%\RemoteDesktop because the MSI's major upgrade removes the old product's files from Program Files.
MachineSettings.AddConfigurationSource(builder.Configuration);

// Off Windows the controlled end runs as the person whose desktop it serves, so there is one settings file between
// them rather than the two accounts Windows has.
if (!OperatingSystem.IsWindows())
{
    UserSettings.AddConfigurationSource(builder.Configuration, dataDirectory);
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
        // Only this process writes this file, so the regular sink works and its periodic flush keeps the log on
        // disk while the service runs — the shared sink would ignore the flush interval entirely.
        .WriteTo.File(Path.Combine(dataDirectory, "logs", "service-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, flushToDiskInterval: TimeSpan.FromSeconds(1), formatProvider: CultureInfo.InvariantCulture)
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
        .AddRemoteDesktopHostFeatures(dataDirectory)
        .AddLocalHostControl(isService: true)
        .AddHostedService<HostControlServer>();
}
else if (OperatingSystem.IsMacOS())
{
    // No service/agent split: TCC gives screen recording and input to an app running in the user's graphical session,
    // so this process is the whole host. It is packaged as a bundle (RemoteDesktop Host.app) with LSUIElement, which
    // is what gives macOS a stable identity to remember the grants by.
    MacHostPermissions.Request(dataDirectory);

    builder.Services
        .AddMacOSPlatform(dataDirectory)
        .AddQuicTransport(transport)
        .AddRemoteDesktopApplication(telemetry)
        .AddRemoteDesktopMedia()
        .AddRemoteDesktopHostFeatures(dataDirectory)
        .AddLocalHostControl(isService: true)
        .AddHostedService<HostControlServer>();
}

await builder.Build().RunAsync().ConfigureAwait(false);
return 0;
