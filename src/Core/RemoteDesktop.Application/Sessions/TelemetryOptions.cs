namespace RemoteDesktop.Application.Sessions;

/// <summary>
/// Whether this device reports session quality to the server, and how often.
/// </summary>
/// <remarks>
/// Off by default, and that default is the feature: a remote desktop that phones home about its sessions without being
/// asked is a surveillance tool, whatever the fields are. Turning it on is a deployment decision, and the client's
/// privacy section says exactly what leaves the machine — a session id, the role, the mode, the link type, an RTT, the
/// platform and the product version. No device name, no address, no window title, no input.
/// </remarks>
public sealed class TelemetryOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Telemetry";

    /// <summary>When false, nothing is ever sent and no timer is started.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// How often one open session reports, in seconds. A number rather than a <see cref="TimeSpan"/> because the
    /// configuration binder does not convert <c>30</c> into one, and an operator writing <c>30</c> should not silently
    /// get the default.
    /// </summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>The interval, clamped to something a console can use: at least 5 s, at most an hour.</summary>
    public TimeSpan Interval => TimeSpan.FromSeconds(Math.Clamp(IntervalSeconds, 5, 3600));
}
