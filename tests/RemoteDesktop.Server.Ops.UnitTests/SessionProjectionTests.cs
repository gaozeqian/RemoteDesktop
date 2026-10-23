using System.Text.Json;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops.UnitTests;

/// <summary>
/// The projection is the only place that decides what a session's life looks like. Each test is one event against the
/// state it should produce, including the ones that must change nothing.
/// </summary>
public class SessionProjectionTests
{
    private const string Correlation = "0f8fad5b-d9cb-469f-a165-70867728950e";

    [Fact]
    public void A_request_creates_the_session_with_both_device_ids()
    {
        var state = SessionProjection.Apply(null, Event(OpsEventKinds.SessionRequested, new { controllerDeviceId = 111u, hostDeviceId = 222u }));

        Assert.NotNull(state);
        Assert.Equal(Correlation, state.CorrelationId);
        Assert.Equal(111u, state.ControllerDeviceId);
        Assert.Equal(222u, state.HostDeviceId);
        Assert.Equal("signaling", state.Source);
        Assert.NotNull(state.StartedAt);
        Assert.Null(state.EndedAt);
    }

    [Fact]
    public void An_accept_keeps_the_original_start_time()
    {
        var requested = SessionProjection.Apply(null, Event(OpsEventKinds.SessionRequested, new { controllerDeviceId = 1u, hostDeviceId = 2u }));
        var accepted = SessionProjection.Apply(requested, Event(OpsEventKinds.SessionAccepted, new { hostDeviceId = 2u }, seconds: 5));

        Assert.Equal(requested!.StartedAt, accepted!.StartedAt);
        Assert.Equal(requested.Id, accepted.Id);
    }

    [Fact]
    public void Relay_pairing_marks_the_transport_and_the_source_node()
    {
        var state = SessionProjection.Apply(null, Event(OpsEventKinds.RelayPaired, new { relaySessionId = Correlation }));

        Assert.NotNull(state);
        Assert.Equal("relay", state.Transport);
        Assert.Equal("relay", state.Source);
        Assert.Equal("relay-sh-01", state.RelayNodeId);
    }

    [Fact]
    public void Relay_close_records_bytes_in_the_direction_they_travelled()
    {
        var state = SessionProjection.Apply(
            null,
            Event(OpsEventKinds.RelayClosed, new { bytesToHost = 1000L, bytesToController = 2500L, reason = "idle" }));

        Assert.Equal(1000, state!.BytesIn);
        Assert.Equal(2500, state.BytesOut);
        Assert.Equal("idle", state.EndReason);
        Assert.NotNull(state.EndedAt);
    }

    /// <summary>
    /// The relay's idle sweep runs about two minutes after a disconnect. It is the only source of the byte counters,
    /// but it is not a later end: the clients already said when and why the session closed.
    /// </summary>
    [Fact]
    public void A_relay_close_after_a_client_end_keeps_the_earlier_end_and_still_records_bytes()
    {
        var ended = SessionProjection.Apply(
            null,
            Event(OpsEventKinds.SessionEnded, new { reason = "closed" }, seconds: 30));
        var closed = SessionProjection.Apply(
            ended,
            Event(OpsEventKinds.RelayClosed, new { bytesToHost = 1000L, bytesToController = 2500L, reason = "idle" }, seconds: 120));

        Assert.Equal(30_000, closed!.EndedAt);
        Assert.Equal("closed", closed.EndReason);
        Assert.Equal(1000, closed.BytesIn);
        Assert.Equal(2500, closed.BytesOut);
    }

    [Fact]
    public void A_failure_after_a_client_end_does_not_move_the_end_later()
    {
        var ended = SessionProjection.Apply(
            null,
            Event(OpsEventKinds.SessionEnded, new { reason = "closed" }, seconds: 30));
        var failed = SessionProjection.Apply(
            ended,
            Event(OpsEventKinds.SessionFailed, new { reason = "timeout" }, seconds: 120));

        Assert.Equal(30_000, failed!.EndedAt);
        Assert.Equal("closed", failed.EndReason);
    }

    [Fact]
    public void A_client_end_report_closes_the_session_with_its_reason()
    {
        var requested = SessionProjection.Apply(
            null,
            Event(OpsEventKinds.SessionRequested, new { controllerDeviceId = 1u, hostDeviceId = 2u }));
        var ended = SessionProjection.Apply(
            requested,
            Event(OpsEventKinds.SessionEnded, new { sessionId = "0123456789abcdef0123456789abcdef", role = "controller", reason = "closed" }, seconds: 30));

        Assert.Equal(30_000, ended!.EndedAt);
        Assert.Equal("closed", ended.EndReason);
        Assert.Equal(1u, ended.ControllerDeviceId);
    }

    [Fact]
    public void Two_end_reports_keep_the_earliest_time_and_the_first_reason()
    {
        var first = SessionProjection.Apply(null, Event(OpsEventKinds.SessionEnded, new { reason = "closed" }, seconds: 30));
        var second = SessionProjection.Apply(first, Event(OpsEventKinds.SessionEnded, new { reason = "idle" }, seconds: 20));

        Assert.Equal(20_000, second!.EndedAt);
        Assert.Equal("closed", second.EndReason);
    }

    [Fact]
    public void A_failure_ends_the_session_with_its_reason()
    {
        var state = SessionProjection.Apply(null, Event(OpsEventKinds.SessionFailed, new { reason = "timeout" }));

        Assert.Equal("timeout", state!.EndReason);
        Assert.NotNull(state.EndedAt);
    }

    [Fact]
    public void An_unknown_kind_leaves_the_session_alone()
    {
        var existing = SessionProjection.Apply(null, Event(OpsEventKinds.SessionRequested, new { controllerDeviceId = 1u, hostDeviceId = 2u }));
        var after = SessionProjection.Apply(existing, Event("session.future_thing", new { something = 1 }));

        Assert.Same(existing, after);
    }

    [Fact]
    public void A_malformed_payload_leaves_the_session_alone()
    {
        var existing = SessionProjection.Apply(null, Event(OpsEventKinds.SessionRequested, new { controllerDeviceId = 1u, hostDeviceId = 2u }));
        var broken = Event(OpsEventKinds.SessionAccepted, new { hostDeviceId = 2u }) with { Payload = "{not json" };

        var after = SessionProjection.Apply(existing, broken);

        Assert.Same(existing, after);
    }

    [Fact]
    public void Events_arriving_out_of_order_do_not_move_the_start_time_forward()
    {
        var paired = SessionProjection.Apply(null, Event(OpsEventKinds.RelayPaired, new { }, seconds: 10));
        var requested = SessionProjection.Apply(paired, Event(OpsEventKinds.SessionRequested, new { controllerDeviceId = 7u }, seconds: 0));

        Assert.Equal(0, requested!.StartedAt);
        Assert.Equal(7u, requested.ControllerDeviceId);
        Assert.Equal("relay", requested.Transport);
    }

    [Fact]
    public void A_heartbeat_is_what_creates_a_direct_session_the_relay_never_saw()
    {
        var state = SessionProjection.Apply(
            null,
            Event(OpsEventKinds.SessionTelemetry, new
            {
                sessionId = "0123456789abcdef0123456789abcdef",
                role = "controller",
                mode = "control",
                transport = "direct",
                rttMilliseconds = 18,
                platform = (string?)null,
                version = "1.2.3",
            }));

        Assert.NotNull(state);
        Assert.Equal("client", state.Source);
        Assert.Equal("direct", state.Transport);
        Assert.Equal("control", state.Mode);
        Assert.Equal(18d, state.RttMs);
        Assert.Equal("1.2.3", state.Version);
        Assert.Null(state.Platform);
    }

    [Fact]
    public void A_heartbeat_adds_quality_to_the_relay_row_without_claiming_it()
    {
        var paired = SessionProjection.Apply(null, Event(OpsEventKinds.RelayPaired, new { }, seconds: 5));
        var enriched = SessionProjection.Apply(
            paired,
            Event(OpsEventKinds.SessionTelemetry, new { role = "host", mode = "view", transport = "relayed", platform = "windows", version = "1.2.3" }, seconds: 20));

        Assert.Equal("relay", enriched!.Source);
        Assert.Equal("relay", enriched.Transport);   // the heartbeat says "relayed"; the column keeps one spelling
        Assert.Equal("windows", enriched.Platform);
        Assert.Equal(5_000, enriched.StartedAt);
    }

    [Fact]
    public void A_host_heartbeat_does_not_erase_the_round_trip_the_controller_measured()
    {
        var controller = SessionProjection.Apply(
            null,
            Event(OpsEventKinds.SessionTelemetry, new { role = "controller", mode = "control", transport = "direct", rttMilliseconds = 42 }, seconds: 0));
        var host = SessionProjection.Apply(
            controller,
            Event(OpsEventKinds.SessionTelemetry, new { role = "host", mode = "control", transport = "direct", rttMilliseconds = 0, platform = "linux" }, seconds: 10));

        Assert.Equal(42d, host!.RttMs);
        Assert.Equal("linux", host.Platform);
    }

    private static OpsEventEnvelope Event(string kind, object payload, int seconds = 0) => new()
    {
        V = 1,
        EventId = Guid.NewGuid().ToString("D"),
        Node = kind.StartsWith("session.relay", StringComparison.Ordinal) ? "relay-sh-01" : "rdvz-sh-01",
        Kind = kind,
        OccurredAt = DateTimeOffset.UnixEpoch.AddSeconds(seconds),
        CorrelationId = Correlation,
        Payload = JsonSerializer.Serialize(payload, OpsEventJson.Options),
    };
}
