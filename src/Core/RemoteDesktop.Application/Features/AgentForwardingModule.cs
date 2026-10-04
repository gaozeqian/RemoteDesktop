using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Agent;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Application.Features;

/// <summary>
/// Host-service side of a feature whose work has to happen inside the interactive session (screen, input): the
/// service authorizes the channel (the dispatcher checked <see cref="RequiredPermission"/>), then relays its frames to
/// the agent, which runs the real feature module.
/// </summary>
/// <remarks>
/// The controller's stream outlives any single agent. When the agent is replaced (the input desktop switched to the
/// login screen or a UAC prompt) the module opens a channel to the new agent and carries on, so the controller never
/// notices more than a short freeze. Frames are relayed whole, in both directions, so a dying agent can never leave a
/// half-written frame in the controller's stream.
/// <para>
/// A <c>lossless</c> channel (file transfer) carries data that must arrive complete and in order, so it behaves
/// differently: the queue holds the controller back instead of dropping frames, nothing is discarded as stale, and when
/// the agent goes away the channel ends (the controller retries the operation) rather than continuing on a new agent
/// that knows nothing about the transfer in progress.
/// </para>
/// </remarks>
public sealed partial class AgentForwardingModule(
    ChannelType channelType,
    Permissions requiredPermission,
    AgentChannelKind agentKind,
    bool replayFirstFrame,
    IAgentGateway gateway,
    ILogger<AgentForwardingModule> logger,
    int maxInboundFrame = AgentForwardingModule.DefaultMaxInboundFrame,
    bool lossless = false) : IFeatureModule
{
    /// <summary>Frames waiting for an agent; the oldest are dropped if the agent is away for long.</summary>
    private const int QueueCapacity = 256;

    /// <summary>Lossless channels hold the controller back early: only a few chunks of file data wait here.</summary>
    private const int LosslessQueueCapacity = 4;

    /// <summary>Controller → host messages are mostly small requests and input events; clipboard sharing needs more.</summary>
    public const int DefaultMaxInboundFrame = 64 * 1024;

    /// <summary>
    /// Input older than this when an agent finally takes it is dropped: replaying a click or keystroke seconds later,
    /// possibly on another desktop, would act blindly.
    /// </summary>
    private static readonly TimeSpan MaxStaleness = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinGenerationTime = TimeSpan.FromMilliseconds(250);

    public ChannelType ChannelType => channelType;

    public Permissions RequiredPermission => requiredPermission;

    public async Task HandleAsync(IChannel channel, SessionContext session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(session);

        var queue = Channel.CreateBounded<QueuedFrame>(new BoundedChannelOptions(lossless ? LosslessQueueCapacity : QueueCapacity)
        {
            FullMode = lossless ? BoundedChannelFullMode.Wait : BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
        var state = new ForwardState();
        using var inbound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reader = ReadInboundAsync(channel.Stream, queue.Writer, state, inbound.Token);

        try
        {
            while (!cancellationToken.IsCancellationRequested && !state.InboundEnded)
            {
                Stream agentStream;
                try
                {
                    agentStream = await gateway.OpenChannelAsync(agentKind, session.Id, cancellationToken).ConfigureAwait(false);
                }
                catch (AgentUnavailableException ex)
                {
                    LogAgentUnavailable(logger, agentKind, ex.Message);
                    if (lossless)
                    {
                        break;
                    }

                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var started = DateTime.UtcNow;
                var controllerGone = await RunGenerationAsync(agentStream, channel.Stream, queue.Reader, state, cancellationToken).ConfigureAwait(false);
                if (controllerGone || lossless)
                {
                    break;
                }

                if (!state.InboundEnded)
                {
                    LogAgentReplaced(logger, agentKind);
                    if (DateTime.UtcNow - started < MinGenerationTime)
                    {
                        await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            queue.Writer.TryComplete();

            // Ending early (agent lost) leaves the reader waiting for a controller that may say nothing more.
            await inbound.CancelAsync().ConfigureAwait(false);
            await reader.ConfigureAwait(false);
        }
    }

    /// <summary>Relays until the agent goes away or the controller does. Returns <c>true</c> for the latter.</summary>
    private async Task<bool> RunGenerationAsync(
        Stream agentStream,
        Stream controllerStream,
        ChannelReader<QueuedFrame> queue,
        ForwardState state,
        CancellationToken sessionToken)
    {
        using var generation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        Task<bool> fromAgent;
        Task toAgent;

        await using (agentStream.ConfigureAwait(false))
        {
            try
            {
                if (replayFirstFrame && state.First is { } first)
                {
                    // The feature's opening request (e.g. which display to stream) is what a new agent waits for.
                    await FrameCodec.WriteAsync(agentStream, first.Frame, sessionToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return false;
            }

            toAgent = PumpToAgentAsync(queue, agentStream, replayFirstFrame ? state.First?.Sequence : null, lossless, generation.Token);
            fromAgent = PumpFromAgentAsync(agentStream, controllerStream, generation.Token, sessionToken);
            await Task.WhenAny(toAgent, fromAgent).ConfigureAwait(false);
            await generation.CancelAsync().ConfigureAwait(false);

            // Disposing the pipe (end of this block) unblocks a read that is still pending on it.
        }

        await toAgent.ConfigureAwait(false);
        return await fromAgent.ConfigureAwait(false);
    }

    /// <param name="replayedSequence">The opening request, when it was already written to this agent on its own.</param>
    private static async Task PumpToAgentAsync(ChannelReader<QueuedFrame> queue, Stream agent, long? replayedSequence, bool keepStale, CancellationToken generation)
    {
        try
        {
            while (await queue.WaitToReadAsync(generation).ConfigureAwait(false))
            {
                while (queue.TryRead(out var queued))
                {
                    if (queued.Sequence == replayedSequence || (!keepStale && Stopwatch.GetElapsedTime(queued.Timestamp) > MaxStaleness))
                    {
                        continue;
                    }

                    await FrameCodec.WriteAsync(agent, queued.Frame, generation).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    /// <returns><c>true</c> when the controller side failed (so no new agent would help).</returns>
    private static async Task<bool> PumpFromAgentAsync(Stream agent, Stream controller, CancellationToken generation, CancellationToken sessionToken)
    {
        try
        {
            while (await FrameCodec.ReadAsync(agent, cancellationToken: generation).ConfigureAwait(false) is { } frame)
            {
                try
                {
                    await FrameCodec.WriteAsync(controller, frame, sessionToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
#pragma warning disable CA1031 // The transport may throw its own exception types; any failure here means the controller is gone.
                catch (Exception)
#pragma warning restore CA1031
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException or OperationCanceledException)
        {
        }

        return false;
    }

    private async Task ReadInboundAsync(Stream controller, ChannelWriter<QueuedFrame> queue, ForwardState state, CancellationToken cancellationToken)
    {
        var sequence = 0L;
        try
        {
            while (await FrameCodec.ReadAsync(controller, maxInboundFrame, cancellationToken).ConfigureAwait(false) is { } frame)
            {
                var queued = new QueuedFrame(frame, ++sequence, Stopwatch.GetTimestamp());
                state.First ??= queued;
                if (lossless)
                {
                    await queue.WriteAsync(queued, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    queue.TryWrite(queued);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException or OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // The transport may throw its own exception types; the channel is over either way.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            state.InboundEnded = true;
            queue.TryComplete();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No agent for the {Kind} channel yet: {Reason}")]
    private static partial void LogAgentUnavailable(ILogger logger, AgentChannelKind kind, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "The agent of the {Kind} channel went away; reconnecting to its replacement.")]
    private static partial void LogAgentReplaced(ILogger logger, AgentChannelKind kind);

    private sealed record QueuedFrame(Frame Frame, long Sequence, long Timestamp);

    private sealed class ForwardState
    {
        private volatile QueuedFrame? _first;
        private volatile bool _inboundEnded;

        /// <summary>The first frame the controller sent, kept for replay to each new agent.</summary>
        public QueuedFrame? First
        {
            get => _first;
            set => _first = value;
        }

        public bool InboundEnded
        {
            get => _inboundEnded;
            set => _inboundEnded = value;
        }
    }
}
