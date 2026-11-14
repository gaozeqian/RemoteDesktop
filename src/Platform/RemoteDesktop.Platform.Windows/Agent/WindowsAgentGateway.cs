using RemoteDesktop.Domain;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Agent;

namespace RemoteDesktop.Platform.Windows.Agent;

public sealed class AgentGatewayOptions
{
    /// <summary>Full path of the session agent executable; defaults to the one next to the service.</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>Where the agent keeps its log; passed on the command line.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>How long a launched agent may take to connect back.</summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long opening a channel waits for an agent that is still starting.</summary>
    public TimeSpan ChannelWait { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Keeps one agent running in the active user session (preferring the console when a user is logged on there), on the
/// desktop that currently has the input: the user's own, or the secure one (login, lock screen, UAC). The agent itself
/// notices when the input desktop changes and says so; the supervisor then replaces it with one of the other kind. It
/// also follows the session across logon, logoff, RDP connect/disconnect and fast user switching, and restarts a
/// crashed agent with a growing pause.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsAgentGateway(AgentGatewayOptions options, ILogger<WindowsAgentGateway> logger) : BackgroundService, IAgentGateway
{
    private static readonly TimeSpan SessionPoll = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan HealthyRun = TimeSpan.FromSeconds(10);

    private AgentState _state = new(AgentStatus.Stopped, DesktopKind.User);
    private volatile AgentConnection? _current;
    private volatile AgentSessionIdentity? _currentSession;

    public AgentState State => _state;

    public AgentSessionIdentity? CurrentSession => _currentSession;

    public event EventHandler? StateChanged;

    public async Task<Stream> OpenChannelAsync(AgentChannelKind kind, SessionId session, AccessGrant grant, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + options.ChannelWait;
        while (true)
        {
            if (_current is { } connection)
            {
                try
                {
                    return await connection.OpenChannelAsync(kind, session, grant, cancellationToken).ConfigureAwait(false);
                }
                catch (AgentUnavailableException) when (DateTime.UtcNow < deadline)
                {
                    // The agent went away while the channel was opening; its replacement is on the way.
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new AgentUnavailableException(State.Detail ?? $"The session agent is {State.Status}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var executable = options.ExecutablePath ?? Path.Combine(AppContext.BaseDirectory, "RemoteDesktop.SessionAgent.exe");
        var desktop = DesktopKind.User;
        var failures = 0;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var sessionId = SessionProcessLauncher.GetActiveSessionId();
                if (sessionId == SessionProcessLauncher.NoSession)
                {
                    SetState(AgentStatus.Unavailable, desktop, "No interactive session yet.");
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var started = Stopwatch.StartNew();
                try
                {
                    var run = await RunAgentAsync(executable, sessionId, desktop, stoppingToken).ConfigureAwait(false);
                    desktop = run.Next;

                    // Only an agent that died young counts against the back-off; switching desktops or sessions is normal.
                    failures = run.Planned || started.Elapsed >= HealthyRun ? 0 : failures + 1;
                }
                catch (NoUserLoggedOnException)
                {
                    // Nobody is logged on: the login screen is all there is to show.
                    desktop = DesktopKind.Secure;
                    continue;
                }
                catch (AgentUnavailableException ex)
                {
                    SetState(AgentStatus.Unavailable, desktop, ex.Message);
                    failures++;
                }
#pragma warning disable CA1031 // Whatever went wrong with one agent must not stop the supervisor.
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    LogAgentFailed(logger, desktop, ex);
                    SetState(AgentStatus.Unavailable, desktop, ex.Message);
                    failures++;
                }

                // 0.5 s after a clean switch; growing up to 10 s while agents keep failing.
                var pause = failures == 0 ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(Math.Min(10, 1 << Math.Min(failures, 4)));
                await Task.Delay(pause, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            SetState(AgentStatus.Stopped, desktop, null);
        }
    }

    /// <summary>
    /// Runs one agent until it should be replaced. Returns the desktop the next one should serve, and whether the
    /// replacement was planned (the input moved or the console changed hands) rather than a death.
    /// </summary>
    private async Task<(DesktopKind Next, bool Planned)> RunAgentAsync(string executable, uint sessionId, DesktopKind desktop, CancellationToken stoppingToken)
    {
        using var prepared = SessionProcessLauncher.Prepare(desktop, sessionId);
        var clients = prepared.User is { } user ? new[] { user } : [];
        var pipeName = $"RemoteDesktop.Agent.{Guid.NewGuid():N}";

        await using var connection = new AgentConnection(pipeName, clients, logger);
        var arguments = $"--agent-pipe {pipeName} --desktop {desktop.ToString().ToLowerInvariant()}" +
                        (options.DataDirectory is { } directory ? $" --data-directory \"{directory}\"" : string.Empty);
        SetState(AgentStatus.Starting, desktop, null);
        using var agent = SessionProcessLauncher.Start(prepared, executable, arguments);
        connection.ExpectProcess(agent.Id);
        LogAgentStarted(logger, agent.Id, desktop, sessionId);

        var nextDesktop = desktop;
        var planned = false;
        var switchRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.DesktopChanged += (_, reported) =>
        {
            if (reported != desktop)
            {
                nextDesktop = reported;
                switchRequested.TrySetResult();
            }
        };
        connection.SecureAttentionRequested += (_, _) => SecureAttention.Send(logger);

        using var run = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var exited = agent.WaitForExitAsync(run.Token);
        try
        {
            var connected = await Task.WhenAny(connection.Connected, exited, Task.Delay(options.StartTimeout, run.Token)).ConfigureAwait(false);
            if (connected != connection.Connected)
            {
                throw new AgentUnavailableException(agent.HasExited
                    ? $"The agent exited with code {agent.ExitCode} before connecting."
                    : "The agent did not connect in time.");
            }

            _currentSession = new AgentSessionIdentity(sessionId, prepared.User?.Value);
            _current = connection;
            SetState(AgentStatus.Running, desktop, null);

            var sessionChanged = WatchSessionAsync(sessionId, run.Token);
            await Task.WhenAny(exited, connection.Closed, switchRequested.Task, sessionChanged).ConfigureAwait(false);
            planned = switchRequested.Task.IsCompleted || sessionChanged.IsCompleted;
            if (switchRequested.Task.IsCompleted)
            {
                LogDesktopSwitch(logger, desktop, nextDesktop);
            }
        }
        finally
        {
            _current = null;
            _currentSession = null;
            await run.CancelAsync().ConfigureAwait(false);
            KillAgent(agent);
            SetState(AgentStatus.Stopped, desktop, null);
        }

        return (nextDesktop, planned);
    }

    private static async Task WatchSessionAsync(uint sessionId, CancellationToken cancellationToken)
    {
        try
        {
            while (SessionProcessLauncher.GetActiveSessionId() == sessionId)
            {
                await Task.Delay(SessionPoll, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void KillAgent(Process agent)
    {
        try
        {
            if (!agent.HasExited)
            {
                agent.Kill(entireProcessTree: true);
                agent.WaitForExit(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LogKillFailed(logger, ex.Message);
        }
    }

    private void SetState(AgentStatus status, DesktopKind desktop, string? detail)
    {
        var state = new AgentState(status, desktop, detail);
        if (state == _state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Started agent process {ProcessId} for the {Desktop} desktop of session {Session}.")]
    private static partial void LogAgentStarted(ILogger logger, int processId, DesktopKind desktop, uint session);

    [LoggerMessage(Level = LogLevel.Information, Message = "The input moved from the {From} to the {To} desktop; replacing the agent.")]
    private static partial void LogDesktopSwitch(ILogger logger, DesktopKind from, DesktopKind to);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The agent for the {Desktop} desktop failed.")]
    private static partial void LogAgentFailed(ILogger logger, DesktopKind desktop, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not stop the agent process: {Reason}")]
    private static partial void LogKillFailed(ILogger logger, string reason);
}
