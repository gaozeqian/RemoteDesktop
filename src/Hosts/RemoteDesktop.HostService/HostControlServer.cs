using System.IO.Pipes;
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
        var pipeName = configuration["HostControlPipe"] ?? HostControlProtocol.PipeName;
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

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            await HostControlHandler.HandleAsync(pipe, control, logger, stoppingToken).ConfigureAwait(false);
        }
    }
}
