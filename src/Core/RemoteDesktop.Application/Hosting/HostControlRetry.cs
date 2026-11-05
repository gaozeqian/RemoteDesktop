using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace RemoteDesktop.Application.Hosting;

/// <summary>
/// Keeps the control endpoint (pipe or socket) coming back after it fails, instead of letting the failure end the
/// service.
/// </summary>
/// <remarks>
/// The control endpoint is a convenience for the local UI; the remote sessions the service hosts do not depend on it.
/// A listener that cannot be created (a socket directory that is a symbolic link, an address that is too long) or that
/// breaks while accepting must therefore cost the UI, not the host: an exception that escapes a hosted service stops
/// the whole process by default. The failure is logged loudly and the endpoint is tried again, with a pause that
/// doubles up to <see cref="MaxDelay"/> so that a failure that never goes away is neither a busy loop nor a flood of log
/// lines, and that starts over once a connection has been accepted again.
/// </remarks>
public static partial class HostControlRetry
{
    /// <summary>The pause after the first failure.</summary>
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(1);

    /// <summary>The longest pause between two attempts.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs <paramref name="serve"/> until <paramref name="stoppingToken"/> is cancelled, starting it again after a
    /// failure of the endpoint (I/O, socket or access errors). Cancellation is never swallowed as a failure.
    /// </summary>
    /// <param name="serve">
    /// Creates the endpoint and accepts connections on it. It must call the action it is given every time a connection
    /// has been accepted: that is what tells this method the endpoint works, so the next failure starts from a short
    /// pause again.
    /// </param>
    /// <param name="logger">Where each failure is reported.</param>
    /// <param name="name">What the endpoint is called in the log (its pipe name or socket address).</param>
    /// <param name="stoppingToken">Cancelled when the service stops.</param>
    /// <param name="delay">How to wait between attempts; the clock by default, replaceable by a test.</param>
    public static async Task RunAsync(
        Func<Action, CancellationToken, Task> serve,
        ILogger logger,
        string name,
        CancellationToken stoppingToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(serve);
        ArgumentNullException.ThrowIfNull(logger);

        delay ??= static (time, token) => Task.Delay(time, token);
        var next = InitialDelay;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await serve(() => next = InitialDelay, stoppingToken).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException)
                {
                    LogFailed(logger, ex, name, next);
                    var wait = next;
                    next = TimeSpan.FromTicks(Math.Min(next.Ticks * 2, MaxDelay.Ticks));
                    await delay(wait, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The service is stopping.
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The host control endpoint {Name} failed; the local UI cannot reach the service until it is back. Trying again in {Delay}.")]
    private static partial void LogFailed(ILogger logger, Exception exception, string name, TimeSpan delay);
}
