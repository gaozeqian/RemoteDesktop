using System.IO.Pipes;
using System.Runtime.Versioning;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Platform.Windows;

namespace RemoteDesktop.HostService;

/// <summary>
/// Lets the UI, running as the logged-in user, read this device's code, temporary password and sessions from the
/// service and rotate the password. Open to interactive users of this machine, not to the network.
/// </summary>
internal sealed class HostControlServer(IHostControl control, IConfiguration configuration, ILogger<HostControlServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var name = configuration["HostControlPipe"] ?? HostControlProtocol.PipeName;

        if (OperatingSystem.IsWindows())
        {
            await ServePipesAsync(name, stoppingToken).ConfigureAwait(false);
        }
        else
        {
            await ServeSocketAsync(HostControlEndpoint.Resolve(name), stoppingToken).ConfigureAwait(false);
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task ServePipesAsync(string pipeName, CancellationToken stoppingToken)
    {
        NamedPipeServerStream? server = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                server = WindowsPipes.CreateServer(pipeName, [WindowsPipes.AuthenticatedUsers]);
                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                var connected = server;
                server = null;
                _ = ServeAsync(connected, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (server is not null)
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The same conversation over a Unix domain socket, which is a stream like the pipe and needs no ACL: the socket
    /// lives in a directory only this user can enter.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private async Task ServeSocketAsync(string address, CancellationToken stoppingToken)
    {
        await using var listener = new HostControlSocketListener(address);
        while (!stoppingToken.IsCancellationRequested)
        {
            var stream = await listener.AcceptAsync(stoppingToken).ConfigureAwait(false);
            _ = ServeAsync(stream, stoppingToken);
        }
    }

    private async Task ServeAsync(Stream stream, CancellationToken stoppingToken)
    {
        await using (stream.ConfigureAwait(false))
        {
            await HostControlHandler.HandleAsync(stream, control, logger, stoppingToken).ConfigureAwait(false);
        }
    }
}
