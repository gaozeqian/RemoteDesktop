using RemoteDesktop.Server.Rendezvous;
using RemoteDesktop.Server.Shared;

// The installer runs this executable for its setup step; the server itself is not started then. The install directory
// is where this executable sits one folder down from, which is the layout the installer puts it in.
if (SetupCommands.TryRun(args, Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)) ?? AppContext.BaseDirectory, Console.Out, out var setupExitCode))
{
    return setupExitCode;
}

// Rendezvous server: device registration and id allocation, presence, signaling and hole-punch coordination.
var builder = WebApplication.CreateBuilder(args);

// On Windows the program lives under Program Files and the live settings under ProgramData; layer the latter over
// the shipped defaults. On Linux and macOS the service already runs with the live settings as its working directory.
ServerSetup.AddConfigurationSource(builder.Configuration, ServerSetup.RendezvousFolder);

// Runs as a console app, under systemd on Linux, or as a Windows service (sc.exe create ...). Registered only for
// the platform it is on: both calls are no-ops elsewhere, but naming one that cannot apply says the wrong thing to
// whoever reads this next.
if (OperatingSystem.IsWindows())
{
    builder.Host.UseWindowsService(options => options.ServiceName = "RemoteDesktopRendezvous");
}
else if (OperatingSystem.IsLinux())
{
    builder.Host.UseSystemd();
}
builder.Services.AddRendezvous(builder.Configuration);

var app = builder.Build();
app.MapRendezvous();

await app.RunAsync().ConfigureAwait(false);
return 0;
