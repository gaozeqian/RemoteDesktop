using System.IO.Pipes;
using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Agent;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Platform.Windows;

namespace RemoteDesktop.HostService;

/// <summary>
/// Lets the UI, running as the logged-in user, read this device's code, temporary password and sessions from the
/// service and rotate the password. Open to interactive users of this machine, not to the network.
/// </summary>
internal sealed partial class HostControlServer(
    IHostControl control,
    IConfiguration configuration,
    ILogger<HostControlServer> logger,
    IAgentGateway? agent = null) : BackgroundService
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
            // The first instance of every attempt must be the first instance of the name. The pipe name is fixed and
            // well known, and the account running the UI could otherwise create it while this service is not holding
            // it (at boot, or in the pause after a crash) and be connected to by the UI in the service's place.
            // Instances created later in the same attempt, to accept the next client, are not "first".
            var first = true;
            while (!stoppingToken.IsCancellationRequested)
            {
                server = CreateInstance(pipeName, first);
                first = false;
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
    /// Creates one instance of the control pipe. When the name is already taken by another process the failure says
    /// so, because <see cref="HostControlRetry"/> logs the exception and nothing else would explain it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateInstance(string pipeName, bool first)
    {
        try
        {
            return WindowsPipes.CreateServer(pipeName, [WindowsPipes.AuthenticatedUsers], firstInstance: first);
        }
        catch (Exception ex) when (first && ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"The pipe name '{pipeName}' is already in use by another process; the service will not serve the host control endpoint on a name it does not own.",
                ex);
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
                void Authorize()
                {
                    if (OperatingSystem.IsWindows()
                        && stream is NamedPipeServerStream pipe
                        && !WindowsPipes.IsAuthorizedHostControlClient(pipe, agent?.CurrentSession))
                    {
                        LogClientRefused(logger);
                        throw new UnauthorizedAccessException("The caller is outside the currently served Windows session.");
                    }
                }

                await HostControlHandler.HandleAsync(stream, control, logger, Authorize, stoppingToken).ConfigureAwait(false);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a host-control caller outside the current agent session.")]
    private static partial void LogClientRefused(ILogger logger);
}
