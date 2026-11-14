using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Hosting;

/// <summary>
/// The start of every server process: build the web host, layer the live settings over the shipped ones, and register
/// the same executable as a Windows service or a systemd unit where those exist.
/// </summary>
/// <remarks>
/// Each host keeps what makes it different — its listeners, health endpoint and options validation. This only removes
/// the part that was copied four times and had already drifted once (the gateway's settings folder, for example).
/// </remarks>
public static class ServerHost
{
    /// <summary>
    /// Creates a builder with the live settings of <paramref name="roleFolder"/> layered over the shipped defaults.
    /// </summary>
    /// <param name="args">Command-line arguments, passed through to the host builder.</param>
    /// <param name="roleFolder">The folder under the server configuration directory, e.g. <c>Rendezvous</c>.</param>
    /// <param name="windowsServiceName">
    /// The name the installer registers the Windows service under. Null leaves the process a plain console app on every
    /// platform; on Linux the same value registers the process with systemd.
    /// </param>
    /// <param name="options">
    /// Overrides for the builder, such as the console's published <c>wwwroot</c>. Null uses the framework's defaults.
    /// </param>
    public static WebApplicationBuilder CreateBuilder(
        string[] args,
        string roleFolder,
        string? windowsServiceName = null,
        WebApplicationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleFolder);

        var builder = options is null
            ? WebApplication.CreateBuilder(args)
            : WebApplication.CreateBuilder(options);

        // On Windows the program lives under Program Files and the live settings under ProgramData; layer the latter
        // over the shipped defaults. On Linux and macOS the service already runs with the live settings as its working
        // directory. This runs before any host binds its options, so what they read is what a server actually uses.
        ServerSetup.AddConfigurationSource(builder.Configuration, roleFolder);

        if (!string.IsNullOrEmpty(windowsServiceName))
        {
            // Both calls are no-ops on the other platform, but naming one that cannot apply says the wrong thing to
            // whoever reads this next.
            if (OperatingSystem.IsWindows())
            {
                builder.Host.UseWindowsService(service => service.ServiceName = windowsServiceName);
            }
            else if (OperatingSystem.IsLinux())
            {
                builder.Host.UseSystemd();
            }
        }

        return builder;
    }
}
