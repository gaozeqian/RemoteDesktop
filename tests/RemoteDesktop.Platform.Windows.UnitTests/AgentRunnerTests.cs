extern alias SessionAgent;

using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Agent;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Platform.Windows.Agent;
using RemoteDesktop.Protocol;
using SessionAgent::RemoteDesktop.SessionAgent;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Platform.Windows.UnitTests;

/// <summary>
/// The agent's run loop with both ends of the pipe in this process, as in <see cref="AgentIpcTests"/>.
/// </summary>
public sealed class AgentRunnerTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("rd-agent-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewPipeName() => $"RemoteDesktop.Agent.Test.{Guid.NewGuid():N}";

    private static SecurityIdentifier[] CurrentUser() => [WindowsIdentity.GetCurrent().User!];

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>
    /// The agent is what serves the file module when the host runs as a service, so it is also what has to run the
    /// sweeper that deletes the partial files nobody came back for. Registering the hosted service is not enough:
    /// nothing runs it until the host is started.
    /// </summary>
    [Fact]
    public async Task Starting_the_agent_runs_the_hosted_services_so_an_abandoned_partial_file_is_swept()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Named pipe ACLs are Windows only.");
        var abandoned = Path.Combine(_folder, "movie.mp4.123-abc" + PartialFile.Extension);
        File.WriteAllText(abandoned, "half a file");
        File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow - PartialFileSweeper.Retention - TimeSpan.FromDays(1));
        var ledgerPath = Path.Combine(_folder, "partial-files.log");
        File.WriteAllLines(ledgerPath, [abandoned]);

        await using var connection = new AgentConnection(NewPipeName(), CurrentUser(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        connection.ExpectProcess(Environment.ProcessId);
        await using var link = await AgentLink.ConnectAsync(connection.PipeName, Ct);

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IUserContextGuard>(new AllowEverything())
            .AddFileTransferFeature(partialFileLedgerPath: ledgerPath)
            .AddSingleton(link)
            .AddSingleton<AgentRunner>();
        using var host = builder.Build();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = AgentRunner.RunHostedAsync(host, stop.Token);

        await WaitUntilAsync(() => !File.Exists(abandoned), TimeSpan.FromSeconds(10));
        Assert.False(File.Exists(abandoned));

        // Stopping the agent stops the host as well, without hanging or throwing.
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    /// <summary>
    /// A channel that does not honour cancellation must not take the agent down: the run loop gives it a couple of
    /// seconds to finish, then returns instead of letting the timeout escape as a fatal error.
    /// </summary>
    [Fact]
    public async Task A_channel_that_ignores_cancellation_is_abandoned_after_the_grace_period_without_an_error()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Named pipe ACLs are Windows only.");
        await using var connection = new AgentConnection(NewPipeName(), CurrentUser(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        connection.ExpectProcess(Environment.ProcessId);
        await using var link = await AgentLink.ConnectAsync(connection.PipeName, Ct);
        var module = new StuckModule();
        var logger = new CapturingLogger();
        var runner = new AgentRunner(link, [module], logger);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = runner.RunAsync(stop.Token);
        await using var channel = await connection.OpenChannelAsync(
            AgentChannelKind.Input,
            SessionId.New(),
            AccessGrant.Create(SessionMode.Control, Permissions.All, Permissions.All),
            Ct);
        await module.Entered.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var clock = Stopwatch.StartNew();
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(15), Ct); // does not throw

        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(10));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
        module.Release();
    }

    /// <summary>
    /// The service-side dispatcher refuses these channels first; this test bypasses it on purpose and asks the agent
    /// directly, which is what a regression or a bypass on the service side would look like from the agent's end.
    /// </summary>
    [Theory]
    [InlineData(AgentChannelKind.Input, ChannelType.Input, Permissions.ControlInput)]
    [InlineData(AgentChannelKind.File, ChannelType.FileTransfer, Permissions.FileTransfer)]
    [InlineData(AgentChannelKind.Terminal, ChannelType.Terminal, Permissions.Terminal)]
    public async Task The_agent_refuses_a_channel_the_session_grant_does_not_allow(
        AgentChannelKind kind,
        ChannelType type,
        Permissions permission)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Named pipe ACLs are Windows only.");
        await using var connection = new AgentConnection(NewPipeName(), CurrentUser(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        connection.ExpectProcess(Environment.ProcessId);
        await using var link = await AgentLink.ConnectAsync(connection.PipeName, Ct);
        var module = new RecordingModule(type, permission);
        var runner = new AgentRunner(link, [module], new CapturingLogger());

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = runner.RunAsync(stop.Token);

        var watch = AccessGrant.Create(SessionMode.View, Permissions.All, Permissions.All);
        await Assert.ThrowsAsync<AgentRefusedException>(
            () => connection.OpenChannelAsync(kind, SessionId.New(), watch, Ct));
        Assert.False(module.Entered.IsCompleted, "The module ran for a channel the grant did not allow.");

        // A normal control session is unaffected: the same request with the full grant reaches the module.
        await using var channel = await connection.OpenChannelAsync(
            kind,
            SessionId.New(),
            AccessGrant.Create(SessionMode.Control, Permissions.All, Permissions.All),
            Ct);
        await module.Entered.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < timeout)
        {
            await Task.Delay(50, Ct);
        }

        Assert.True(condition(), $"Timed out after {timeout} waiting for the condition.");
    }

    private sealed class AllowEverything : IUserContextGuard
    {
        public string? GetRefusalReason() => null;
    }

    /// <summary>Records that it was entered, then waits until the test stops the agent.</summary>
    private sealed class RecordingModule(ChannelType type, Permissions permission) : IFeatureModule
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChannelType ChannelType => type;

        public Permissions RequiredPermission => permission;

        public Task Entered => _entered.Task;

        public async Task HandleAsync(IChannel channel, SessionContext session, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Takes a channel and never lets go of it, whatever it is told.</summary>
    private sealed class StuckModule : IFeatureModule
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChannelType ChannelType => ChannelType.Input;

        public Permissions RequiredPermission => Permissions.ControlInput;

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public Task HandleAsync(IChannel channel, SessionContext session, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            return _release.Task;
        }
    }

    private sealed class CapturingLogger : ILogger<AgentRunner>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
