using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using RemoteDesktop.Application;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.HostService;
using RemoteDesktop.Platform.Windows;
using RemoteDesktop.Platform.Windows.Agent;
using RemoteDesktop.Transport;
using Serilog;

[assembly: SupportedOSPlatform("windows")]

// Composition root of the host side: the privileged, always-on service that accepts connections and authorizes
// sessions. Screen and input live in the session agent it supervises; the service only relays their channels.
if (!OperatingSystem.IsWindows())
{
    throw new PlatformNotSupportedException("Only Windows is supported in phase 1.");
}

const string ServiceName = SetupCommands.ServiceName;

// The installer runs this executable for its setup and cleanup steps; the service itself is not started then.
if (SetupCommands.TryRun(
        args,
        AppContext.BaseDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteDesktop"),
        Console.Out,
        out var setupExitCode))
{
    return setupExitCode;
}

var builder = Host.CreateApplicationBuilder(args);
builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));

var dataDirectory = builder.Configuration["DataDirectory"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteDesktop");

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

var agent = builder.Configuration.GetSection("Agent").Get<AgentGatewayOptions>() ?? new AgentGatewayOptions();
agent.DataDirectory = dataDirectory;

// A service has no console; keep a rolling log next to the identity.
builder.Logging.ClearProviders().AddSerilog(
    new LoggerConfiguration()
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
        .WriteTo.File(Path.Combine(dataDirectory, "logs", "service-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, shared: true, formatProvider: CultureInfo.InvariantCulture)
        .CreateLogger(),
    dispose: true);

builder.Services
    .AddWindowsService(options => options.ServiceName = ServiceName)
    .AddWindowsPlatform(dataDirectory, DataProtectionScope.LocalMachine)
    .AddWindowsAgentGateway(agent)
    .AddQuicTransport(transport)
    .AddRemoteDesktopApplication()
    .AddAgentForwardedDesktop()
    .AddLocalHostControl(isService: true)
    .AddHostedService<HostControlServer>();

await builder.Build().RunAsync().ConfigureAwait(false);
return 0;
