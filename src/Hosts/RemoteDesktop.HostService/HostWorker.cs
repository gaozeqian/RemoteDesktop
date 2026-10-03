namespace RemoteDesktop.HostService;

/// <summary>
/// Service main loop. Milestone M1 adds rendezvous registration and the peer listener; M3 adds agent supervision.
/// </summary>
internal sealed partial class HostWorker(ILogger<HostWorker> logger) : BackgroundService
{
    public const string ServiceName = "RemoteDesktopHost";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, ServiceName);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{ServiceName} started.")]
    private static partial void LogStarted(ILogger logger, string serviceName);
}
