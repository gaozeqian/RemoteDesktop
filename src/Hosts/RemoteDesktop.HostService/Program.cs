using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using RemoteDesktop.Application;
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

const string ServiceName = "RemoteDesktopHost";

var builder = Host.CreateApplicationBuilder(args);
builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));

var dataDirectory = builder.Configuration["DataDirectory"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteDesktop");
var transport = builder.Configuration.GetSection("Transport").Get<TransportOptions>() ?? new TransportOptions();
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
