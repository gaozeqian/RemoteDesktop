using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Signaling;
using RemoteDesktop.Signaling.Messages;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// Operator commands the signaling server forwards to a device: "disconnect this session" ends it, anything else is
/// logged and ignored, and a bad command never stops the loop that reads the next one.
/// </summary>
public sealed class AdminCommandServiceTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_disconnect_command_ends_the_named_session_and_logs_the_command_id()
    {
        await using var host = await TestDevice.StartAsync(server);
        await using var controller = await TestDevice.StartAsync(server);
        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Closed += (_, _) => closed.TrySetResult();
        await WaitForAsync(() => host.HostSessions.ActiveSessions.Count == 1, "the session to appear on the host");
        var id = host.HostSessions.ActiveSessions.Single().Id;
        var commands = new CommandSource();
        var log = new CapturingLogger();
        using var service = new AdminCommandService(commands, host.HostSessions, log);
        await service.StartAsync(Ct);

        commands.Send(Disconnect("cmd-1", id.ToString()));

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await WaitForAsync(() => host.HostSessions.ActiveSessions.Count == 0, "the session to disappear from the host");
        // Cancelling the session is what closes it, and that can complete on another thread before the loop writes its
        // line; wait for the line rather than assuming it is already there.
        await WaitForAsync(() => log.Messages.Any(m => m.Level == LogLevel.Information), "the command to be logged");
        var line = Assert.Single(log.Messages, m => m.Level == LogLevel.Information);
        Assert.Equal("cmd-1", line.Field("CommandId"));
        Assert.Equal(id, Assert.IsType<SessionId>(line.Field("SessionId")));
        Assert.Equal(true, line.Field("WasOpen"));
        await WaitForAsync(() => commands.Results.Count == 1, "the command acknowledgment");
        Assert.True(commands.Results.Single().Succeeded);
        Assert.Equal("cmd-1", commands.Results.Single().CommandId);

        await service.StopAsync(Ct);
    }

    [Fact]
    public async Task Commands_that_are_not_a_disconnect_of_a_well_formed_session_id_leave_the_session_alone()
    {
        await using var host = await TestDevice.StartAsync(server);
        await using var controller = await TestDevice.StartAsync(server);
        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Closed += (_, _) => closed.TrySetResult();
        await WaitForAsync(() => host.HostSessions.ActiveSessions.Count == 1, "the session to appear on the host");
        var id = host.HostSessions.ActiveSessions.Single().Id;
        var commands = new CommandSource();
        var log = new CapturingLogger();
        using var service = new AdminCommandService(commands, host.HostSessions, log);
        await service.StartAsync(Ct);

        commands.Send(new AdminCommand { CommandId = "c1", Command = "reboot", SessionId = id.ToString() });
        // The name is compared exactly.
        commands.Send(new AdminCommand { CommandId = "c2", Command = "DISCONNECT_SESSION", SessionId = id.ToString() });
        commands.Send(new AdminCommand { CommandId = "c3", Command = "", SessionId = id.ToString() });
        // The id has to be the 32-hex form both ends share.
        commands.Send(Disconnect("c4", "not-a-session"));
        commands.Send(Disconnect("c5", ""));
        commands.Send(Disconnect("c6", id.Value.ToString("D")));

        // The commands are handled in order, so once all six are accounted for the session has had its chance to go.
        await WaitForAsync(() => log.Messages.Count(m => m.Level == LogLevel.Warning) == 6, "every command to be handled");
        Assert.False(closed.Task.IsCompleted);
        Assert.Single(host.HostSessions.ActiveSessions);
        Assert.All(log.Messages, m => Assert.Equal(LogLevel.Warning, m.Level));
        Assert.Equal("c1", log.Messages[0].Field("CommandId"));
        Assert.Equal("reboot", log.Messages[0].Field("Command"));

        // The loop is still reading.
        commands.Send(Disconnect("c7", id.ToString()));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await service.StopAsync(Ct);
    }

    [Fact]
    public async Task A_disconnect_for_a_session_that_is_already_gone_is_a_success_and_the_service_goes_on()
    {
        await using var host = await TestDevice.StartAsync(server);
        var commands = new CommandSource();
        var log = new CapturingLogger();
        using var service = new AdminCommandService(commands, host.HostSessions, log);
        await service.StartAsync(Ct);
        var gone = SessionId.New();

        commands.Send(Disconnect("gone-1", gone.ToString()));
        commands.Send(Disconnect("gone-2", gone.ToString()));

        await WaitForAsync(() => log.Messages.Count == 2, "both commands to be handled");
        Assert.All(log.Messages, m =>
        {
            Assert.Equal(LogLevel.Information, m.Level);
            Assert.Equal(false, m.Field("WasOpen"));
        });
        Assert.False(service.ExecuteTask!.IsCompleted);

        await service.StopAsync(Ct);
    }

    [Fact]
    public async Task Without_a_rendezvous_client_the_service_ends_at_once()
    {
        await using var host = await TestDevice.StartAsync(server);
        using var service = new AdminCommandService(rendezvous: null, host.HostSessions, new CapturingLogger());

        await service.StartAsync(Ct);

        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    private static AdminCommand Disconnect(string commandId, string sessionId) =>
        new() { CommandId = commandId, Command = ClientCapabilities.Commands.DisconnectSession, SessionId = sessionId };

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(50, Ct);
        }
    }

    private sealed class CapturingLogger : ILogger<AdminCommandService>
    {
        private readonly ConcurrentQueue<LogEntry> _messages = new();

        public IReadOnlyList<LogEntry> Messages => [.. _messages];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // The structured fields are the contract; the rendered text is only how a particular sink shows them.
            var fields = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, object?>();

            _messages.Enqueue(new LogEntry(logLevel, fields));
        }
    }

    private sealed record LogEntry(LogLevel Level, IReadOnlyDictionary<string, object?> Fields)
    {
        public object? Field(string name) => Fields.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>Only the admin command stream does anything; the rest of the client is never called.</summary>
    private sealed class CommandSource : IRendezvousClient
    {
        private readonly Channel<AdminCommand> _commands = Channel.CreateUnbounded<AdminCommand>();

        public ConcurrentQueue<AdminResult> Results { get; } = new();

        public RendezvousState State => RendezvousState.Online;

        public DeviceId? DeviceId => null;

        public IPEndPoint? ReflectorEndPoint => null;

        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public void Send(AdminCommand command) => _commands.Writer.TryWrite(command);

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public IAsyncEnumerable<IncomingConnection> ReadIncomingAsync(CancellationToken cancellationToken) =>
            Empty<IncomingConnection>();

        public IAsyncEnumerable<AdminCommand> ReadAdminCommandsAsync(CancellationToken cancellationToken) =>
            _commands.Reader.ReadAllAsync(cancellationToken);

        public Task ReportAdminResultAsync(AdminResult result, CancellationToken cancellationToken)
        {
            Results.Enqueue(result);
            return Task.CompletedTask;
        }

        public Task<ConnectReady> RequestConnectionAsync(DeviceId target, IEnumerable<PeerEndpoint> candidates, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlySet<DeviceId>?> QueryPresenceAsync(IReadOnlyList<DeviceId> deviceIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<DeviceId>?>(null);

        public Task AcceptAsync(string requestId, IEnumerable<PeerEndpoint> candidates, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RejectAsync(string requestId, CancellationToken cancellationToken) => Task.CompletedTask;

        public void ReportTelemetry(TelemetryReport report)
        {
        }

        public void ReportSessionEnded(SessionEndedReport report)
        {
        }

        private static async IAsyncEnumerable<T> Empty<T>()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
