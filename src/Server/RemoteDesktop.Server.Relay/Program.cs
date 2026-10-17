using System.Net;
using Microsoft.Extensions.Options;
using RemoteDesktop.Server.Relay;
using RemoteDesktop.Server.Shared;

// Relay server: pairs peers by relay ticket and forwards their UDP datagrams without decrypting them. The HTTP face
// exists only for monitoring — a relay that could not be scraped was the one blind spot in the console's first plan.
var builder = WebApplication.CreateBuilder(args);

if (OperatingSystem.IsWindows())
{
    builder.Host.UseWindowsService(options => options.ServiceName = "RemoteDesktopRelay");
}
else if (OperatingSystem.IsLinux())
{
    builder.Host.UseSystemd();
}

builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection(RelayOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);

var relayOptions = builder.Configuration.GetSection(RelayOptions.SectionName).Get<RelayOptions>() ?? new RelayOptions();
builder.Services.AddOpsEvents(builder.Configuration, relayOptions.Name);

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<RelayOptions>>().Value;
    var key = Convert.FromBase64String(options.SharedKey);
    if (key.Length < 32)
    {
        throw new InvalidOperationException("Relay:SharedKey must be a base64 key of at least 32 bytes.");
    }

    return new UdpRelay(new IPEndPoint(IPAddress.Any, options.Port), key, options.IdleTimeout,
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<IOpsEventSink>(),
        options.Name,
        sp.GetRequiredService<ILogger<UdpRelay>>());
});
builder.Services.AddHostedService<RelayWorker>();

builder.WebHost.UseUrls(relayOptions.HttpEndpoint);
builder.Services.AddSingleton(new NodeInfo(
    relayOptions.Name,
    "relay",
    typeof(UdpRelay).Assembly.GetName().Version?.ToString() ?? "0.0.0",
    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapGet("/metrics", (HttpContext context, UdpRelay relay, IOptions<RelayOptions> options) =>
    NodeTokenGuard.IsAuthorized(context.Request.Headers.Authorization.ToString(), options.Value.NodeToken)
        ? Results.Text(RelayMetrics.Render(relay.Snapshot()), RelayMetrics.ContentType)
        : Results.Unauthorized());

app.MapGet("/internal/v1/info", (HttpContext context, NodeInfo info, IOptions<RelayOptions> options) =>
    NodeTokenGuard.IsAuthorized(context.Request.Headers.Authorization.ToString(), options.Value.NodeToken)
        ? Results.Ok(info)
        : Results.Unauthorized());

await app.RunAsync().ConfigureAwait(false);

internal sealed class RelayWorker(UdpRelay relay) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => relay.RunAsync(stoppingToken);
}
