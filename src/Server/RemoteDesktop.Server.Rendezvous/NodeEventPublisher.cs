using System.Text.Json;
using Microsoft.Extensions.Options;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>
/// Builds the envelopes this process sends to the console: one place that knows the node's name, the envelope version
/// and that event ids are generated here rather than by whoever observed the fact.
/// </summary>
/// <remarks>
/// The sink is a no-op when no console is configured, so <see cref="Enabled"/> is the only branch a caller needs —
/// and the only one it should write, since building a payload for nobody is pure waste on the connect path.
/// </remarks>
public sealed class NodeEventPublisher(IOpsEventSink sink, IOptions<RendezvousOptions> options, TimeProvider time)
{
    /// <summary>Whether a console is configured at all.</summary>
    public bool Enabled => sink.Enabled;

    /// <summary>Queues one fact. Never blocks: the sink owns its own bounded queue.</summary>
    public void Publish(string kind, Guid correlationId, object payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!sink.Enabled)
        {
            return;
        }

        sink.Publish(new OpsEventEnvelope
        {
            EventId = Guid.CreateVersion7().ToString("D"),
            Node = options.Value.Name,
            Kind = kind,
            OccurredAt = time.GetUtcNow(),
            CorrelationId = correlationId.ToString("D"),
            Payload = JsonSerializer.Serialize(payload, OpsEventJson.Options),
        });
    }
}
