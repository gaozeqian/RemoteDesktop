using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Agent;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Platform.Windows.Agent;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.SessionAgent;

/// <summary>
/// The agent's work: serve the feature channels the host service asks for, and tell the service when the input moves to
/// another desktop (login screen, UAC) so it can replace this agent with one that can see it.
/// </summary>
internal sealed partial class AgentRunner(AgentLink link, IEnumerable<IFeatureModule> modules, ILogger<AgentRunner> logger)
{
    private static readonly TimeSpan DesktopPoll = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ChannelGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HostStopTimeout = TimeSpan.FromSeconds(5);

    private readonly Dictionary<ChannelType, IFeatureModule> _modules = modules.ToDictionary(m => m.ChannelType);

    /// <summary>
    /// Runs the agent inside its host. Registering a hosted service does nothing by itself: the sweeper of abandoned
    /// partial files (and anything else the features add) only runs once the host is started.
    /// </summary>
    public static async Task RunHostedAsync(IHost host, CancellationToken stopping)
    {
        ArgumentNullException.ThrowIfNull(host);
        await host.StartAsync(stopping).ConfigureAwait(false);
        try
        {
            await host.Services.GetRequiredService<AgentRunner>().RunAsync(stopping).ConfigureAwait(false);
        }
        finally
        {
            // Bounded: a hosted service that does not stop must not keep an agent the service is waiting on alive.
            using var stopTimeout = new CancellationTokenSource(HostStopTimeout);
            try
            {
                await host.StopAsync(stopTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Runs until the service shuts the agent down or the link closes.</summary>
    public async Task RunAsync(CancellationToken stopping)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var watcher = WatchInputDesktopAsync(run.Token);
        var channels = new List<Task>();
        try
        {
            await foreach (var request in link.ReadRequestsAsync(run.Token).ConfigureAwait(false))
            {
                channels.Add(ServeAsync(request, run.Token));
                channels.RemoveAll(t => t.IsCompleted);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await run.CancelAsync().ConfigureAwait(false);
            await watcher.ConfigureAwait(false);
            try
            {
                await Task.WhenAll(channels).WaitAsync(ChannelGrace, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A channel that ignores cancellation is abandoned; the process is about to exit anyway.
                LogChannelsStuck(logger, ChannelGrace.TotalSeconds);
            }
        }
    }

    private async Task ServeAsync(AgentChannelRequest request, CancellationToken cancellationToken)
    {
        var type = request.Kind switch
        {
            AgentChannelKind.Video => ChannelType.Video,
            AgentChannelKind.Input => ChannelType.Input,
            AgentChannelKind.Clipboard => ChannelType.Clipboard,
            AgentChannelKind.Audio => ChannelType.Audio,
            AgentChannelKind.File => ChannelType.FileTransfer,
            AgentChannelKind.Terminal => ChannelType.Terminal,
            AgentChannelKind.Camera => ChannelType.Camera,
            _ => throw new InvalidDataException($"Unknown channel kind {request.Kind}."),
        };
        try
        {
            if (!_modules.TryGetValue(type, out var module))
            {
                LogNoModule(logger, request.Kind);
                return;
            }

            var stream = await link.OpenChannelAsync(request, cancellationToken).ConfigureAwait(false);
            await using var channel = new PipeChannel(type, stream);
            var session = new SessionContext(
                request.Session,
                SessionRole.Host,
                new DetachedPeerConnection(),
                AccessGrant.Create(SessionMode.Control, Permissions.All, Permissions.All));
            await module.HandleAsync(channel, session, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // A failing channel must not take the agent, and the other channels, down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogChannelFailed(logger, request.Kind, ex);
        }
    }

    private async Task WatchInputDesktopAsync(CancellationToken cancellationToken)
    {
        DesktopKind? reported = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var current = InputDesktop.GetCurrent();
                if (current != reported)
                {
                    LogInputDesktop(logger, current);
                    await link.ReportDesktopAsync(current, cancellationToken).ConfigureAwait(false);
                    reported = current;
                }

                await Task.Delay(DesktopPoll, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The input desktop is now the {Desktop} one.")]
    private static partial void LogInputDesktop(ILogger logger, DesktopKind desktop);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No module serves the {Kind} channel.")]
    private static partial void LogNoModule(ILogger logger, AgentChannelKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Some channels did not stop within {Seconds} s of the agent shutting down; abandoning them.")]
    private static partial void LogChannelsStuck(ILogger logger, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Kind} channel failed.")]
    private static partial void LogChannelFailed(ILogger logger, AgentChannelKind kind, Exception exception);
}

/// <summary>
/// Leaves the agent's lifetime to <see cref="AgentRunner"/> and the host service. The default console lifetime hooks
/// Ctrl+C and process exit, and on exit waits for the host to be disposed; the agent has no console, and a start of
/// the host must not be able to change how the process ends.
/// </summary>
internal sealed class AgentHostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
