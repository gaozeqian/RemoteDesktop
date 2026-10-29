using RemoteDesktop.Server.Shared;

var builder = WebApplication.CreateBuilder(args);
ServerSetup.AddConfigurationSource(builder.Configuration, ServerSetup.GatewayFolder);

if (OperatingSystem.IsWindows())
{
    builder.Host.UseWindowsService(options => options.ServiceName = "RemoteDesktopGateway");
}
else if (OperatingSystem.IsLinux())
{
    builder.Host.UseSystemd();
}

var listen = builder.Configuration["Gateway:Listen"] ?? ServerSetup.DefaultGatewayListen;
builder.WebHost.UseUrls(listen);

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

// The console's health endpoint is deliberately loopback-only. Do not give the public listener an endpoint that
// appears to offer the same check; the package's service manager monitors the Gateway process itself.
app.MapGet("/health", () => Results.NotFound());
app.MapReverseProxy();

await app.RunAsync().ConfigureAwait(false);
return 0;
