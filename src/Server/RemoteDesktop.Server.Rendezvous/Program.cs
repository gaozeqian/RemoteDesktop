using RemoteDesktop.Server.Rendezvous;

// The installer runs this executable for its setup step; the server itself is not started then. The install directory
// is where this executable sits one folder down from, which is the layout the installer puts it in.
if (SetupCommands.TryRun(args, Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)) ?? AppContext.BaseDirectory, Console.Out, out var setupExitCode))
{
    return setupExitCode;
}

// Rendezvous server: device registration and id allocation, presence, signaling and hole-punch coordination.
var builder = WebApplication.CreateBuilder(args);

// Runs as a console app or as a Windows service (sc.exe create ...); the latter also sends the logs to the event log.
builder.Host.UseWindowsService(options => options.ServiceName = "RemoteDesktopRendezvous");
builder.Services.AddRendezvous(builder.Configuration);

var app = builder.Build();
app.MapRendezvous();

await app.RunAsync().ConfigureAwait(false);
return 0;
