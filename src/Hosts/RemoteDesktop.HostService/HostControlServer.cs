using System.IO.Pipes;
using System.Runtime.Versioning;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Platform.Windows;

namespace RemoteDesktop.HostService;

/// <summary>
/// Lets the UI, running as the logged-in user, read this device's code, temporary password and sessions from the
/// service and rotate the password. Open to interactive users of this machine, not to the network.
/// </summary>
internal sealed partial class HostControlServer(IHostControl control, IConfiguration configuration, ILogger<HostControlServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var name = configuration["HostControlPipe"] ?? HostControlProtocol.PipeName;

        // This endpoint serves the local UI only. If it cannot be created or breaks, the service goes on hosting
        // sessions and tries again; an exception escaping here would stop the whole process.
        await HostControlRetry.RunAsync(
            (accepted, token) => OperatingSystem.IsWindows()
                ? ServePipesAsync(name, accepted, token)
                : ServeSocketAsync(HostControlEndpoint.Resolve(name), accepted, token),
            logger,
            name,
            stoppingToken).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private async Task ServePipesAsync(string pipeName, Action accepted, CancellationToken stoppingToken)
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
                accepted();
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
    private async Task ServeSocketAsync(string address, Action accepted, CancellationToken stoppingToken)
    {
        await using var listener = new HostControlSocketListener(address);
        while (!stoppingToken.IsCancellationRequested)
        {
            var stream = await listener.AcceptAsync(stoppingToken).ConfigureAwait(false);
            accepted();
            _ = ServeAsync(stream, stoppingToken);
        }
    }

    private async Task ServeAsync(Stream stream, CancellationToken stoppingToken)
    {
        // Started and forgotten by the accept loop, so nothing awaits it: whatever escapes would go unobserved.
        try
        {
            await using (stream.ConfigureAwait(false))
            {
                await HostControlHandler.HandleAsync(stream, control, logger, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // One failed request must not take the endpoint, or the service, down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRequestFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A request on the host control endpoint failed.")]
    private static partial void LogRequestFailed(ILogger logger, Exception exception);
}
