using RemoteDesktop.Server.Rendezvous;

// Rendezvous server: device registration and id allocation, presence, signaling and hole-punch coordination.
var builder = WebApplication.CreateBuilder(args);

// Runs as a console app or as a Windows service (sc.exe create ...); the latter also sends the logs to the event log.
builder.Host.UseWindowsService(options => options.ServiceName = "RemoteDesktopRendezvous");
builder.Services.AddRendezvous(builder.Configuration);

var app = builder.Build();
app.MapRendezvous();

await app.RunAsync().ConfigureAwait(false);
