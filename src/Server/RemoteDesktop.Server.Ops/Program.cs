using RemoteDesktop.Server.Ops;
using RemoteDesktop.Server.Shared;

// The installer, or an operator on a fresh box, runs this executable for its setup step; the server itself is not
// started then.
if (SetupCommands.TryRun(args, Console.Out, out var setupExitCode))
{
    return setupExitCode;
}

// The Linux package starts this program with /etc as its working directory so that appsettings.json is read from
// /etc; the console's static assets stay next to the program. The web root is a separate setting, and without this
// the sign-in page is a 404 on every packaged installation. In development the published directory is not there, and
// the default (the project's own wwwroot) is the right one.
var publishedWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var builder = Directory.Exists(publishedWebRoot)
    ? WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, WebRootPath = publishedWebRoot })
    : WebApplication.CreateBuilder(args);

// The console's live settings are next to the program on Linux and macOS, and in ProgramData on Windows (where the
// MSI's major upgrade would otherwise delete them). Add the Windows layer before AddOps binds the options.
ServerSetup.AddConfigurationSource(builder.Configuration, ServerSetup.OpsFolder);

// Same three deployment shapes as the signaling server: console, systemd unit, Windows service.
if (OperatingSystem.IsWindows())
{
    builder.Host.UseWindowsService(options => options.ServiceName = "RemoteDesktopOps");
}
else if (OperatingSystem.IsLinux())
{
    builder.Host.UseSystemd();
}

var listen = builder.Configuration["Ops:Listen"];
if (!string.IsNullOrWhiteSpace(listen))
{
    builder.WebHost.UseUrls(listen);
}

builder.AddOps();

var app = builder.Build();
app.UseOps();

await app.RunAsync().ConfigureAwait(false);
return 0;
