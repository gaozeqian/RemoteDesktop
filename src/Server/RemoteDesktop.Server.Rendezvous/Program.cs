using RemoteDesktop.Server.Rendezvous;

// Rendezvous server: device registration and id allocation, presence, signaling and hole-punch coordination.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRendezvous(builder.Configuration);

var app = builder.Build();
app.MapRendezvous();

await app.RunAsync().ConfigureAwait(false);
