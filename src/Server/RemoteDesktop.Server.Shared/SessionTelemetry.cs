using System.Text.Json;
using System.Text.RegularExpressions;

namespace RemoteDesktop.Server.Shared;

/// <summary>
/// One heartbeat from a client about one of its own sessions, after validation.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place where a client's word reaches the console's database, so the shape is closed: fixed sets
/// for role, mode, transport and platform, a 32-hex id, an RTT bounded by what a live session can plausibly measure,
/// and a version string of letters, digits and separators. Anything else is refused with a reason, and that reason is
/// the only thing that goes to the log — not the value, which a hostile client controls.
/// </para>
/// <para>
/// Both ends of a session report under the same <see cref="SessionId"/> (the controller announces it in
/// <c>Hello.session_id</c>), which is what keeps one session one row. <see cref="RelaySessionId"/>, when present, is
/// the id the relay already uses, so the console can enrich that record instead of adding a second one.
/// </para>
/// </remarks>
public sealed partial record SessionTelemetry
{
    /// <summary>
    /// Longest version string accepted. The product's informational version carries the commit hash appended after
    /// <c>+</c>, which makes the ordinary build about 46 characters — a limit of 32 refused every real heartbeat.
    /// </summary>
    public const int MaxVersionLength = 64;

    /// <summary>Largest RTT accepted. Beyond this the session is not alive; the number is a client's invention.</summary>
    public const int MaxRttMilliseconds = 60_000;

    public required string SessionId { get; init; }

    public string? RelaySessionId { get; init; }

    /// <summary><c>controller</c> or <c>host</c>.</summary>
    public required string Role { get; init; }

    /// <summary><c>control</c> or <c>view</c>.</summary>
    public required string Mode { get; init; }

    /// <summary><c>direct</c> or <c>relayed</c>.</summary>
    public required string Transport { get; init; }

    /// <summary>Controller-side RTT; 0 from a host, which does not measure it.</summary>
    public int RttMilliseconds { get; init; }

    /// <summary>Host-side platform, or <c>null</c> from a controller.</summary>
    public string? Platform { get; init; }

    /// <summary>Version of the reporting process, or <c>null</c>.</summary>
    public string? Version { get; init; }

    /// <summary>Validates a report from the wire. Returns false with a short reason rather than throwing.</summary>
    public static bool TryCreate(
        string? sessionId,
        string? relaySessionId,
        string? role,
        string? mode,
        string? transport,
        int rttMilliseconds,
        string? platform,
        string? version,
        out SessionTelemetry telemetry,
        out string error)
    {
        telemetry = null!;
        error = string.Empty;

        if (!TryCanonicalize(sessionId, out var canonicalSessionId))
        {
            error = "session id is not a UUID";
            return false;
        }

        string? canonicalRelayId = null;
        if (!string.IsNullOrEmpty(relaySessionId) && !TryCanonicalize(relaySessionId, out canonicalRelayId))
        {
            error = "relay session id is not a UUID";
            return false;
        }

        var normalizedRole = role?.Trim().ToLowerInvariant();
        if (normalizedRole is not ("controller" or "host"))
        {
            error = "role is neither controller nor host";
            return false;
        }

        var normalizedMode = mode?.Trim().ToLowerInvariant();
        if (normalizedMode is not ("control" or "view"))
        {
            error = "mode is neither control nor view";
            return false;
        }

        var normalizedTransport = transport?.Trim().ToLowerInvariant();
        if (normalizedTransport is not ("direct" or "relayed"))
        {
            error = "transport is neither direct nor relayed";
            return false;
        }

        if (rttMilliseconds is < 0 or > MaxRttMilliseconds)
        {
            error = "rtt is out of range";
            return false;
        }

        var normalizedPlatform = string.IsNullOrEmpty(platform) ? null : platform.Trim().ToLowerInvariant();
        if (normalizedPlatform is not (null or "windows" or "macos" or "linux"))
        {
            error = "platform is not one this console knows";
            return false;
        }

        var normalizedVersion = string.IsNullOrEmpty(version) ? null : version.Trim();
        if (normalizedVersion is { Length: > MaxVersionLength } || (normalizedVersion is not null && !VersionShape().IsMatch(normalizedVersion)))
        {
            error = "version is not a product version";
            return false;
        }

        telemetry = new SessionTelemetry
        {
            SessionId = canonicalSessionId,
            RelaySessionId = canonicalRelayId,
            Role = normalizedRole,
            Mode = normalizedMode,
            Transport = normalizedTransport,
            RttMilliseconds = rttMilliseconds,
            Platform = normalizedPlatform,
            Version = normalizedVersion,
        };
        return true;
    }

    /// <summary>The payload the console reads, in the envelope's camelCase shape.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, OpsEventJson.Options);

    /// <summary>Whether a string is a UUID the console can correlate by, in either the dashed or the plain form.</summary>
    public static bool IsGuid(string? value) => TryCanonicalize(value, out _);

    /// <summary>
    /// Parses either UUID spelling into the dashed form, which is what the session table stores and what the relay
    /// writes. Clients send the plain form; the relay's id travels through a proto field where a person may read it.
    /// </summary>
    private static bool TryCanonicalize(string? value, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (!Guid.TryParseExact(value, "N", out var guid) && !Guid.TryParseExact(value, "D", out guid))
        {
            return false;
        }

        canonical = guid.ToString("D");
        return true;
    }

    [GeneratedRegex("^[0-9A-Za-z.+_()-]+$")]
    private static partial Regex VersionShape();
}
