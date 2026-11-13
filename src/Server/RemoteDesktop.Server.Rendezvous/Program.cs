using RemoteDesktop.Server.Hosting;
using RemoteDesktop.Server.Rendezvous;
using RemoteDesktop.Server.Shared;

// The installer runs this executable for its setup step; the server itself is not started then. The install directory
// is where this executable sits one folder down from, which is the layout the installer puts it in.
if (SetupCommands.TryRun(args, Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)) ?? AppContext.BaseDirectory, Console.Out, out var setupExitCode))
{
    return setupExitCode;
}

// Rendezvous server: device registration and id allocation, presence, signaling and hole-punch coordination.
var builder = ServerHost.CreateBuilder(args, ServerSetup.RendezvousFolder, "RemoteDesktopRendezvous");

// A public plaintext signaling listener is a deployment mistake, not a supported default. The check reads the same
// Urls Kestrel will bind, and runs before the host is built so the failure is a sentence rather than a live endpoint.
RendezvousOptions.ValidateListeningUrls(
    builder.Configuration["Urls"],
    builder.Configuration.GetValue("Rendezvous:AllowInsecureSignaling", false));

builder.Services.AddRendezvous(builder.Configuration);

var app = builder.Build();
app.MapRendezvous();

await app.RunAsync().ConfigureAwait(false);
return 0;
