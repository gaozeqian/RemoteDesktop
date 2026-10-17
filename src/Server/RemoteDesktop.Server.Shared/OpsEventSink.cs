using System.Text.Json;

namespace RemoteDesktop.Server.Shared;

/// <summary>
/// Where a server process sends the facts the console cannot see for itself: a connection was requested, accepted,
/// relayed, closed.
/// </summary>
/// <remarks>
/// Publishing must never block the caller and never throw. These calls sit on the signaling and forwarding paths, where
/// a slow or dead console would otherwise become a slow or dead remote-desktop session. The implementation is a bounded
/// queue with a background sender; when the queue is full, events are dropped and counted, not waited on.
/// </remarks>
public interface IOpsEventSink
{
    /// <summary>False when no endpoint is configured — callers may skip building the event entirely.</summary>
    bool Enabled { get; }

    /// <summary>Queues <paramref name="envelope"/> for delivery. Returns immediately.</summary>
    void Publish(OpsEventEnvelope envelope);
}

/// <summary>The sink of a deployment that does not run a console.</summary>
public sealed class NoopOpsEventSink : IOpsEventSink
{
    /// <inheritdoc />
    public bool Enabled => false;

    /// <inheritdoc />
    public void Publish(OpsEventEnvelope envelope)
    {
        // Nothing to do: the console is what reads this, and there is none.
    }
}

/// <summary>The serializer settings both ends use, so a property name cannot drift between sender and receiver.</summary>
public static class OpsEventJson
{
    /// <summary>Web defaults: camelCase, case-insensitive on read.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
