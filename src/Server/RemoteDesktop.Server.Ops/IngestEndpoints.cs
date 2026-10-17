using System.Globalization;
using System.Text.Json;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops;

/// <summary>Marker so the endpoint can take a typed logger.</summary>
public sealed class OpsIngestEndpoint
{
}

/// <summary>
/// Where servers hand over what only they saw: a connection was requested, accepted, paired, closed.
/// </summary>
/// <remarks>
/// Anonymous but signed, and outside <c>/api</c> so the browser's CSRF rule does not apply to a machine caller. The
/// timestamp window is the first line of defence against a captured request; the unique <c>event_id</c> is the second,
/// and the only one that survives a replay inside the window.
/// </remarks>
public static partial class IngestEndpoints
{
    private const int MaxBodyBytes = 1024 * 1024;
    private const int MaxEvents = 500;
    private const int MaxSkewSeconds = 60;

    /// <summary>Maps <c>POST /ingest/events</c>.</summary>
    public static void MapOpsIngest(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/ingest/events", async (
            HttpContext context,
            OpsOptions options,
            OpsStore store,
            TimeProvider time,
            ILogger<OpsIngestEndpoint> logger,
            CancellationToken cancellationToken) =>
        {
            if (options.Ingest.Keys.Count == 0)
            {
                // Nothing to verify against: a console without keys was never meant to receive events.
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (context.Request.ContentLength > MaxBodyBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var keyId = context.Request.Headers[OpsEventSigner.KeyIdHeader].ToString();
            if (!options.Ingest.Keys.TryGetValue(keyId, out var keyBase64))
            {
                return Results.Unauthorized();
            }

            var timestampText = context.Request.Headers[OpsEventSigner.TimestampHeader].ToString();
            if (!long.TryParse(timestampText, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
            {
                return Results.Unauthorized();
            }

            var now = time.GetUtcNow();
            if (Math.Abs(now.ToUnixTimeSeconds() - timestamp) > MaxSkewSeconds)
            {
                return Results.Unauthorized();
            }

            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (buffer.Length > MaxBodyBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var body = buffer.ToArray();
            var signature = context.Request.Headers[OpsEventSigner.SignatureHeader].ToString();
            var key = Convert.FromBase64String(keyBase64);
            if (!OpsEventSigner.Verify(key, timestamp, body, signature))
            {
                return Results.Unauthorized();
            }

            List<OpsEventEnvelope>? events;
            try
            {
                events = JsonSerializer.Deserialize<List<OpsEventEnvelope>>(body, OpsEventJson.Options);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "body" });
            }

            if (events is null || events.Count > MaxEvents)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var invalid = events.FindIndex(envelope =>
                string.IsNullOrWhiteSpace(envelope.EventId)
                || string.IsNullOrWhiteSpace(envelope.Kind)
                || string.IsNullOrWhiteSpace(envelope.CorrelationId)
                || envelope.V != 1);
            if (invalid >= 0)
            {
                return Results.BadRequest(new { error = "event", index = invalid });
            }

            for (var index = 0; index < events.Count; index++)
            {
                if (string.Equals(events[index].Kind, OpsEventKinds.SessionTelemetry, StringComparison.Ordinal))
                {
                    events[index] = CorrelateTelemetry(events[index]);
                }
            }

            var inserted = await store
                .AppendEventsAsync(events, now.ToUnixTimeMilliseconds(), cancellationToken)
                .ConfigureAwait(false);

            Ingested(logger, keyId, events.Count, inserted);
            return Results.Ok(new { accepted = events.Count, inserted });
        }).AllowAnonymous();
    }

    /// <summary>
    /// Points a heartbeat at the session it belongs to: the relay's id when the client reported one, because the relay
    /// has already written events under that id and the console must enrich that record rather than add a second one.
    /// </summary>
    /// <remarks>
    /// A payload that cannot be read is passed through unchanged: the projection ignores it, and refusing the whole
    /// batch over one bad heartbeat would let a single device stop a node's events.
    /// </remarks>
    private static OpsEventEnvelope CorrelateTelemetry(OpsEventEnvelope envelope)
    {
        string? relaySessionId;
        try
        {
            using var payload = JsonDocument.Parse(envelope.Payload);
            relaySessionId = payload.RootElement.TryGetProperty("relaySessionId", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return envelope;
        }

        if (!SessionTelemetry.IsGuid(relaySessionId))
        {
            return envelope;
        }

        // The relay writes its correlation ids in the dashed form; this makes a plain-form id land on the same row.
        return envelope with { CorrelationId = Guid.Parse(relaySessionId!).ToString("D") };
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ingested {Accepted} events with key {KeyId} ({Inserted} new).")]
    private static partial void Ingested(ILogger logger, string keyId, int accepted, int inserted);
}
