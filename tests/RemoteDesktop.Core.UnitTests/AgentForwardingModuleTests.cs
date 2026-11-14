using System.IO.Pipes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Agent;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The service-side relay between a controller's channel and the session agent, with both ends played by the test
/// over real pipes: frames flow whole, and a replaced agent is picked up without the controller noticing.
/// </summary>
public class AgentForwardingModuleTests
{
    private static readonly SessionContext TestSession = new(SessionId.New(), SessionRole.Host, null!, AccessGrant.Create(SessionMode.Control, Permissions.All, Permissions.All));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Frames_flow_both_ways_and_the_module_ends_when_the_controller_leaves()
    {
        await using var rig = await Rig.StartAsync(replayFirstFrame: true);
        var agent = await rig.OfferAgentAsync();

        await rig.ControllerSendsAsync(1, 1, 2, 3);
        Assert.Equal([1, 2, 3], await ReadPayloadAsync(agent));

        await FrameCodec.WriteAsync(agent, new Frame(2, new byte[] { 9, 9 }), Ct);
        Assert.Equal([9, 9], await ReadPayloadAsync(rig.Controller));

        await rig.Controller.DisposeAsync();
        await rig.Run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Null(await FrameCodec.ReadAsync(agent, cancellationToken: Ct)); // the agent's pipe was closed too
    }

    [Fact]
    public async Task Input_sent_just_before_the_agent_is_ready_is_still_delivered()
    {
        await using var rig = await Rig.StartAsync(replayFirstFrame: false);

        await rig.ControllerSendsAsync(1, 0xCC); // the first keystroke races the agent connection
        await Task.Delay(100, Ct);
        var agent = await rig.OfferAgentAsync();

        Assert.Equal([0xCC], await ReadPayloadAsync(agent));
    }

    [Fact]
    public async Task A_replacement_agent_gets_the_opening_request_and_the_controller_keeps_receiving()
    {
        await using var rig = await Rig.StartAsync(replayFirstFrame: true);
        var first = await rig.OfferAgentAsync();
        await rig.ControllerSendsAsync(1, 7, 7);
        Assert.Equal([7, 7], await ReadPayloadAsync(first));

        // The desktop switches: the old agent is gone and a new one is ready.
        var second = await rig.OfferAgentAsync();
        await first.DisposeAsync();

        Assert.Equal([7, 7], await ReadPayloadAsync(second)); // the opening request is replayed
        await FrameCodec.WriteAsync(second, new Frame(2, new byte[] { 5 }), Ct);
        Assert.Equal([5], await ReadPayloadAsync(rig.Controller));

        await rig.ControllerSendsAsync(3, 4);
        Assert.Equal([4], await ReadPayloadAsync(second));
    }

    [Fact]
    public async Task Input_waiting_for_an_agent_is_not_silently_discarded()
    {
        await using var rig = await Rig.StartAsync(replayFirstFrame: false);

        await rig.ControllerSendsAsync(1, 0xAA);
        await Task.Delay(1300, Ct); // input no longer has a stale-frame dropping path
        var agent = await rig.OfferAgentAsync();
        Assert.Equal([0xAA], await ReadPayloadAsync(agent));

        await rig.ControllerSendsAsync(1, 0xBB);
        Assert.Equal([0xBB], await ReadPayloadAsync(agent));
    }

    [Fact]
    public async Task A_replacement_input_agent_accepts_new_input_without_replaying_the_old_generation()
    {
        await using var rig = await Rig.StartAsync(replayFirstFrame: false);
        var agent = await rig.OfferAgentAsync();
        await rig.ControllerSendsAsync(1, 0xAA);
        Assert.Equal([0xAA], await ReadPayloadAsync(agent));

        await agent.DisposeAsync();
        var replacement = await rig.OfferAgentAsync();

        // Input that arrives before the module has picked the replacement up belongs to the dead desktop and is
        // discarded with it (by design), so this waits for the generation to start rather than racing that discard.
        await rig.Gateway.NextTaken.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await rig.ControllerSendsAsync(1, 0xBB);

        Assert.Equal([0xBB], await ReadPayloadAsync(replacement));
    }

    [Fact]
    public async Task A_frame_cut_short_by_a_dying_agent_never_reaches_the_controller()
    {
        await using var rig = await Rig.StartAsync(replayFirstFrame: true);
        var first = await rig.OfferAgentAsync();
        await rig.ControllerSendsAsync(1, 1);
        await ReadPayloadAsync(first);

        // Header announces 100 bytes, only 10 arrive, then the agent dies.
        var header = new byte[FrameCodec.HeaderSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, 100);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 2);
        await first.WriteAsync(header, Ct);
        await first.WriteAsync(new byte[10], Ct);

        var second = await rig.OfferAgentAsync();
        await first.DisposeAsync();
        await ReadPayloadAsync(second); // replayed request: the module is on the new agent
        await FrameCodec.WriteAsync(second, new Frame(2, new byte[] { 42 }), Ct);

        Assert.Equal([42], await ReadPayloadAsync(rig.Controller));
    }

    [Fact]
    public async Task The_module_waits_for_an_agent_that_is_not_ready_yet()
    {
        await using var rig = await Rig.StartAsync(replayFirstFrame: true);
        rig.Gateway.OfferFailure();
        var agent = await rig.OfferAgentAsync();

        await rig.ControllerSendsAsync(1, 3);

        Assert.Equal([3], await ReadPayloadAsync(agent));
    }

    private static async Task<byte[]> ReadPayloadAsync(Stream stream)
    {
        var frame = await FrameCodec.ReadAsync(stream, cancellationToken: Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(15), Ct);
        return frame!.Value.Payload.ToArray();
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop;

        private Rig(QueueGateway gateway, Stream controller, Task run, CancellationTokenSource stop)
        {
            Gateway = gateway;
            Controller = controller;
            Run = run;
            _stop = stop;
        }

        public QueueGateway Gateway { get; }

        /// <summary>The controller's end of its channel.</summary>
        public Stream Controller { get; }

        public Task Run { get; }

        public static async Task<Rig> StartAsync(bool replayFirstFrame)
        {
            var gateway = new QueueGateway();
            var (moduleEnd, controller) = await PipePair.CreateAsync();
            var module = new AgentForwardingModule(
                replayFirstFrame ? ChannelType.Video : ChannelType.Input,
                replayFirstFrame ? Permissions.ViewScreen : Permissions.ControlInput,
                replayFirstFrame ? AgentChannelKind.Video : AgentChannelKind.Input,
                replayFirstFrame,
                gateway,
                NullLogger<AgentForwardingModule>.Instance);

            var stop = new CancellationTokenSource();
            var run = module.HandleAsync(new TestChannel(ChannelType.Video, moduleEnd), TestSession, stop.Token);
            return new Rig(gateway, controller, run, stop);
        }

        /// <summary>Makes a new agent available to the module and returns the agent's end of its pipe.</summary>
        public async Task<Stream> OfferAgentAsync()
        {
            var (serviceEnd, agentEnd) = await PipePair.CreateAsync();
            Gateway.Offer(serviceEnd);
            return agentEnd;
        }

        public ValueTask ControllerSendsAsync(ushort type, params byte[] payload) =>
            FrameCodec.WriteAsync(Controller, new Frame(type, payload), Ct);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await Controller.DisposeAsync();
            try
            {
                await Run.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
            }

            _stop.Dispose();
        }
    }

    private sealed class QueueGateway : IAgentGateway
    {
        private readonly Channel<Func<Stream>> _opens = Channel.CreateUnbounded<Func<Stream>>();
        private TaskCompletionSource _taken = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AgentState State => new(AgentStatus.Running, DesktopKind.User);

        /// <summary>
        /// Completes once the module has picked up the most recently offered channel — the point after which input
        /// belongs to the new generation instead of being discarded with the old one.
        /// </summary>
        public Task NextTaken => _taken.Task;

#pragma warning disable CS0067
        public event EventHandler? StateChanged;
#pragma warning restore CS0067

        public void Offer(Stream serviceEnd)
        {
            _taken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _opens.Writer.TryWrite(() => serviceEnd);
        }

        public void OfferFailure() => _opens.Writer.TryWrite(() => throw new AgentUnavailableException("still starting"));

        public async Task<Stream> OpenChannelAsync(AgentChannelKind kind, SessionId session, AccessGrant grant, CancellationToken cancellationToken)
        {
            var open = await _opens.Reader.ReadAsync(cancellationToken);
            _taken.TrySetResult();
            return open();
        }
    }

    private sealed class TestChannel(ChannelType type, Stream stream) : IChannel
    {
        public ChannelType Type => type;

        public Stream Stream => stream;

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }

    private static class PipePair
    {
        public static async Task<(Stream First, Stream Second)> CreateAsync()
        {
            var name = $"rd-unit-{Guid.NewGuid():N}";
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync());
            return (server, client);
        }
    }
}
