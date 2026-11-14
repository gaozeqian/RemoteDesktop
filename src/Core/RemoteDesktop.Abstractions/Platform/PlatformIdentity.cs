namespace RemoteDesktop.Abstractions.Platform;

/// <summary>The short name telemetry reports for the machine being controlled.</summary>
public interface IPlatformIdentity
{
    /// <summary><c>windows</c>, <c>macos</c> or <c>linux</c>; a value the monitoring console already groups by.</summary>
    string Name { get; }
}

/// <summary>
/// The value each platform layer registers. A record rather than a bare string because the service container resolves
/// by type, and a string keyed by type would collide with every other string a host registers.
/// </summary>
public sealed record PlatformIdentity(string Name) : IPlatformIdentity;
