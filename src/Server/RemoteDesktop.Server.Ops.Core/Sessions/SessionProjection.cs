using System.Text.Json;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops.Core;

/// <summary>
/// A session as the console understands it. One row per connection attempt that reached a host, kept up to date by
/// applying the events about it in order.
/// </summary>
/// <param name="Id">The console's own id, generated when the first event arrives.</param>
/// <param name="CorrelationId">Minted by the signaling server; the same value the relay sees.</param>
/// <param name="Source">Who produced the latest fact: <c>signaling</c> or <c>relay</c>. Never mixed into one count.</param>
/// <param name="RttMs">Controller-measured round trip, when a client reported one for this session.</param>
/// <param name="Platform">Platform of the controlled device, when its host reported one.</param>
public sealed record SessionState(
    string Id,
    string CorrelationId,
    uint? ControllerDeviceId,
    uint? HostDeviceId,
    string? Mode,
    string? Transport,
    string Source,
    string? RelayNodeId,
    long? StartedAt,
    long? EndedAt,
    string? EndReason,
    long? BytesIn,
    long? BytesOut,
    long UpdatedAt,
    double? RttMs = null,
    double? LossPct = null,
    string? Platform = null,
    string? Version = null,
    string? ClientSessionId = null);

/// <summary>
/// Folds one event into a session. Pure, so the rules can be tested without a database or a clock.
/// </summary>
/// <remarks>
/// A session is a projection, not a source of truth: the events are kept beside it, so a wrong rule can be fixed and
/// the projection rebuilt. Unknown kinds are ignored instead of rejected — a newer server sending a kind this console
/// does not know yet must not stop the batch it arrived in.
/// </remarks>
public static class SessionProjection
{
    /// <summary>The session after <paramref name="envelope"/>, or the unchanged input when the event says nothing useful.</summary>
    public static SessionState? Apply(SessionState? current, OpsEventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var at = envelope.OccurredAt.ToUnixTimeMilliseconds();
        JsonDocument payload;
        try
        {
            payload = JsonDocument.Parse(envelope.Payload);
        }
        catch (JsonException)
        {
            // A malformed payload is a bug on the sender, not a reason to lose the session it belonged to.
            return current;
        }

        using (payload)
        {
            var root = payload.RootElement;

            return envelope.Kind switch
            {
                OpsEventKinds.SessionRequested => Merge(current, envelope, at, state => state with
                {
                    ControllerDeviceId = UInt32(root, "controllerDeviceId") ?? state.ControllerDeviceId,
                    HostDeviceId = UInt32(root, "hostDeviceId") ?? state.HostDeviceId,
                    StartedAt = Earliest(state.StartedAt, at),
                }),

                OpsEventKinds.SessionAccepted => Merge(current, envelope, at, state => state with
                {
                    HostDeviceId = UInt32(root, "hostDeviceId") ?? state.HostDeviceId,
                    StartedAt = Earliest(state.StartedAt, at),
                }),

                OpsEventKinds.SessionFailed => Merge(current, envelope, at, state => state with
                {
                    EndedAt = Earliest(state.EndedAt, at),
                    EndReason = state.EndReason ?? String(root, "reason") ?? "failed",
                }),

                // A client's report that the session closed. Both ends send one, and for a direct session this is the
                // only end fact there is — without it the row would stay active for the life of the database.
                OpsEventKinds.SessionEnded => Merge(current, envelope, at, state => state with
                {
                    EndedAt = Earliest(state.EndedAt, at),
                    EndReason = state.EndReason ?? String(root, "reason") ?? "ended",
                }),

                OpsEventKinds.RelayPaired => Merge(current, envelope, at, state => state with
                {
                    Transport = "relay",
                    Source = "relay",
                    RelayNodeId = envelope.Node,
                    StartedAt = Earliest(state.StartedAt, at),
                }),

                // The relay's idle sweep can arrive minutes after both clients have reported the session ended. It is
                // evidence for the byte counters, not a later end: the first end fact and its reason stay, and only
                // the counters are filled in.
                OpsEventKinds.RelayClosed => Merge(current, envelope, at, state => state with
                {
                    Transport = "relay",
                    Source = "relay",
                    RelayNodeId = envelope.Node,
                    EndedAt = Earliest(state.EndedAt, at),
                    EndReason = state.EndReason ?? String(root, "reason") ?? "closed",
                    BytesIn = Int64(root, "bytesToHost") ?? state.BytesIn,
                    BytesOut = Int64(root, "bytesToController") ?? state.BytesOut,
                }),

                // A client heartbeat. It may be the first fact about a direct (P2P) session — the relay never saw it —
                // in which case it is what creates the row, and the row says so by its source. For a relayed session
                // the relay's row already exists and this only adds what the relay cannot see.
                OpsEventKinds.SessionTelemetry => Merge(current, envelope, at, state => state with
                {
                    Mode = String(root, "mode") ?? state.Mode,
                    Transport = TransportName(String(root, "transport")) ?? state.Transport,
                    StartedAt = Earliest(state.StartedAt, at),

                    // Only the controller measures RTT; a host reporting 0 must not erase that reading.
                    RttMs = String(root, "role") == "controller" && Number(root, "rttMilliseconds") is { } rtt && rtt > 0
                        ? rtt
                        : state.RttMs,

                    Platform = String(root, "platform") ?? state.Platform,
                    Version = String(root, "version") ?? state.Version,

                    // The id the host actually knows this session by. For a relayed session the envelope's correlation
                    // is the relay's id, which the host never sees — this is the id a disconnect must address.
                    ClientSessionId = String(root, "sessionId") ?? state.ClientSessionId,
                    Source = current is null ? "client" : state.Source,
                }),

                _ => current,
            };
        }
    }

    private static SessionState Merge(
        SessionState? current,
        OpsEventEnvelope envelope,
        long at,
        Func<SessionState, SessionState> change)
    {
        var state = current ?? new SessionState(
            Id: Guid.NewGuid().ToString("N"),
            CorrelationId: envelope.CorrelationId,
            ControllerDeviceId: null,
            HostDeviceId: null,
            Mode: null,
            Transport: null,
            Source: "signaling",
            RelayNodeId: null,
            StartedAt: null,
            EndedAt: null,
            EndReason: null,
            BytesIn: null,
            BytesOut: null,
            UpdatedAt: at);

        return change(state) with { UpdatedAt = Math.Max(state.UpdatedAt, at) };
    }

    /// <summary>
    /// The earlier of the two: events can arrive out of order, and a session's start is a fact about the world, not
    /// about the order the console happened to receive the facts in.
    /// </summary>
    private static long Earliest(long? current, long candidate) =>
        current is { } existing ? Math.Min(existing, candidate) : candidate;

    private static uint? UInt32(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetUInt32(out var parsed) ? parsed : null;

    private static long? Int64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : null;

    private static double? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetDouble(out var parsed) ? parsed : null;

    /// <summary>
    /// One spelling per link type in the column, whichever source wrote it. The relay's events say <c>relay</c> and a
    /// client heartbeat says <c>relayed</c>; two spellings of the same thing would split every count in the UI.
    /// </summary>
    private static string? TransportName(string? value) => value switch
    {
        "direct" => "direct",
        "relayed" or "relay" => "relay",
        _ => null,
    };

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
