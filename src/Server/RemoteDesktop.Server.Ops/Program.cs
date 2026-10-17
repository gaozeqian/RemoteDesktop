using RemoteDesktop.Server.Ops;

// The installer, or an operator on a fresh box, runs this executable for its setup step; the server itself is not
// started then.
if (SetupCommands.TryRun(args, Console.Out, out var setupExitCode))
{
    return setupExitCode;
}

var builder = WebApplication.CreateBuilder(args);

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
