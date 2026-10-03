// Rendezvous server: device registration and id allocation, presence, signaling and hole-punch coordination.
// Endpoints are added in milestone M1.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/health");

await app.RunAsync().ConfigureAwait(false);
