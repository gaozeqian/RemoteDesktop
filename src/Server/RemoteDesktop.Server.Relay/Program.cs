using System.Net;
using Microsoft.Extensions.Options;
using RemoteDesktop.Server.Relay;

// Relay server: pairs peers by relay ticket and forwards their UDP datagrams without decrypting them.
var builder = Host.CreateApplicationBuilder(args);

// Runs as a console app, under systemd on Linux, or as a Windows service (sc.exe create ...). Each of these is a
// no-op when the process was not started by that manager, but only the one for the platform it is on is registered,
// so a console run says nothing about a manager it could never have been started by.
if (OperatingSystem.IsWindows())
{
    builder.Services.AddWindowsService(options => options.ServiceName = "RemoteDesktopRelay");
}
else if (OperatingSystem.IsLinux())
{
    builder.Services.AddSystemd();
}
builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection(RelayOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<RelayOptions>>().Value;
    var key = Convert.FromBase64String(options.SharedKey);
    if (key.Length < 32)
    {
        throw new InvalidOperationException("Relay:SharedKey must be a base64 key of at least 32 bytes.");
    }

    return new UdpRelay(new IPEndPoint(IPAddress.Any, options.Port), key, options.IdleTimeout,
        sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<UdpRelay>>());
});
builder.Services.AddHostedService<RelayWorker>();

await builder.Build().RunAsync().ConfigureAwait(false);

internal sealed class RelayWorker(UdpRelay relay) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => relay.RunAsync(stoppingToken);
}
