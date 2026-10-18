using System.Text.Json;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops.UnitTests;

/// <summary>
/// The heartbeat is the only payload an end-user device writes into the console, so its validation is a security
/// boundary rather than a convenience. Each refusal here is a shape that would otherwise reach the database.
/// </summary>
public class SessionTelemetryTests
{
    private const string SessionId = "0123456789ABCDEF0123456789ABCDEF";
    private const string RelayId = "0f8fad5b-d9cb-469f-a165-70867728950e";

    [Fact]
    public void A_well_formed_report_is_canonicalised_to_one_uuid_spelling()
    {
        Assert.True(Create(out var telemetry, out var error));

        Assert.Empty(error);
        Assert.Equal("01234567-89ab-cdef-0123-456789abcdef", telemetry.SessionId);
        Assert.Equal(RelayId, telemetry.RelaySessionId);
        Assert.Equal("controller", telemetry.Role);
        Assert.Equal("direct", telemetry.Transport);
        Assert.Equal(23, telemetry.RttMilliseconds);
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("0123456789abcdef0123456789abcde")]   // one character short
    [InlineData("")]
    public void A_session_id_that_is_not_a_uuid_is_refused(string sessionId)
    {
        Assert.False(SessionTelemetry.TryCreate(
            sessionId, null, "controller", "control", "direct", 10, null, "0.3.0", out _, out var error));
        Assert.Contains("session id", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("control")]   // a mode, not a role
    [InlineData("")]
    public void A_role_outside_the_two_the_console_counts_is_refused(string role)
    {
        Assert.False(SessionTelemetry.TryCreate(
            SessionId, null, role, "control", "direct", 10, null, null, out _, out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60_001)]
    public void An_rtt_no_live_session_could_measure_is_refused(int rtt)
    {
        Assert.False(SessionTelemetry.TryCreate(
            SessionId, null, "controller", "control", "direct", rtt, null, null, out _, out var error));
        Assert.Contains("rtt", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_version_that_is_really_a_payload_is_refused()
    {
        Assert.False(SessionTelemetry.TryCreate(
            SessionId, null, "host", "control", "direct", 0, "windows", "<script>alert(1)</script>", out _, out var error));
        Assert.Contains("version", error, StringComparison.Ordinal);

        Assert.False(SessionTelemetry.TryCreate(
            SessionId, null, "host", "control", "direct", 0, "windows", new string('9', 80), out _, out _));
    }

    [Fact]
    public void The_version_the_product_actually_reports_is_accepted()
    {
        // AssemblyInformationalVersion is "0.3.0+<40-char commit hash>"; that is 46 characters, not a payload.
        Assert.True(SessionTelemetry.TryCreate(
            SessionId, null, "host", "control", "direct", 0, "windows",
            "0.3.0+db9820fe0cb5d3dc26e1f292ae8d49c6e886469b", out var telemetry, out var error));
        Assert.Empty(error);
        Assert.StartsWith("0.3.0+", telemetry.Version, StringComparison.Ordinal);
    }

    [Fact]
    public void A_platform_the_page_has_no_label_for_is_refused()
    {
        Assert.False(SessionTelemetry.TryCreate(
            SessionId, null, "host", "control", "direct", 0, "solaris", null, out _, out var error));
        Assert.Contains("platform", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_payload_the_console_reads_uses_the_envelope_spelling()
    {
        Assert.True(Create(out var telemetry, out _));

        using var json = JsonDocument.Parse(telemetry.ToJson());
        var root = json.RootElement;

        // camelCase, because the projection reads the same shape every other event uses.
        Assert.True(root.TryGetProperty("rttMilliseconds", out var rtt));
        Assert.Equal(23, rtt.GetInt32());
        Assert.True(root.TryGetProperty("relaySessionId", out _));
    }

    private static bool Create(out SessionTelemetry telemetry, out string error) =>
        SessionTelemetry.TryCreate(
            SessionId,
            RelayId,
            "controller",
            "control",
            "direct",
            23,
            null,
            "0.3.0",
            out telemetry,
            out error);
}
