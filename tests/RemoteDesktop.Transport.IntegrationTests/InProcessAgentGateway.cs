using System.IO.Pipes;
using RemoteDesktop.Abstractions.Agent;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// Plays the session agent inside the test process: each channel the service opens is served by the real feature
/// module on the far end of a pipe. <see cref="RestartAgentAsync"/> ends everything the current "agent" was serving,
/// like the real agent being replaced when the input moves to the login screen.
/// </summary>
internal sealed class InProcessAgentGateway(IEnumerable<IFeatureModule> agentModules) : IAgentGateway, IDisposable
{
    private readonly List<IFeatureModule> _modules = [.. agentModules];
    private readonly Lock _lock = new();
    private readonly List<Task> _running = [];
    private CancellationTokenSource _generation = new();

    public AgentState State => new(AgentStatus.Running, DesktopKind.User);

#pragma warning disable CS0067
    public event EventHandler? StateChanged;
#pragma warning restore CS0067

    /// <summary>How many agents have existed, starting at 1.</summary>
    public int Generations { get; private set; } = 1;

    public async Task<Stream> OpenChannelAsync(AgentChannelKind kind, SessionId sessionId, CancellationToken cancellationToken)
    {
        var type = kind switch
        {
            AgentChannelKind.Video => ChannelType.Video,
            AgentChannelKind.Input => ChannelType.Input,
            AgentChannelKind.Clipboard => ChannelType.Clipboard,
            AgentChannelKind.File => ChannelType.FileTransfer,
            AgentChannelKind.Terminal => ChannelType.Terminal,
            _ => ChannelType.Audio,
        };
        var module = _modules.First(m => m.ChannelType == type);

        var name = $"rd-agent-{Guid.NewGuid():N}";
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(cancellationToken), client.ConnectAsync(cancellationToken));

        CancellationToken token;
        lock (_lock)
        {
            token = _generation.Token;
        }

        var session = new SessionContext(sessionId, SessionRole.Host, null!, AccessGrant.Create(SessionMode.Control, Permissions.All, Permissions.All));
        var task = Task.Run(async () =>
        {
            try
            {
                await module.HandleAsync(new AgentSideChannel(type, client), session, token);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                await client.DisposeAsync();
            }
        }, CancellationToken.None);

        lock (_lock)
        {
            _running.Add(task);
        }

        return server;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _generation.Cancel();
            _generation.Dispose();
        }
    }

    /// <summary>Ends everything the current agent serves; the next channel request meets a new agent.</summary>
    public async Task RestartAgentAsync()
    {
        CancellationTokenSource old;
        Task[] tasks;
        lock (_lock)
        {
            old = _generation;
            _generation = new CancellationTokenSource();
            tasks = [.. _running];
            _running.Clear();
            Generations++;
        }

        await old.CancelAsync();
        await Task.WhenAll(tasks);
        old.Dispose();
    }

    private sealed class AgentSideChannel(ChannelType type, Stream stream) : IChannel
    {
        public ChannelType Type => type;

        public Stream Stream => stream;

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
