namespace RemoteDesktop.Platform.Shared;

/// <summary>Converts a host-clock stamp (nanoseconds, as the macOS helpers report it) to a <see cref="TimeSpan"/>.</summary>
/// <remarks>
/// Both macOS frame sinks had this one-liner and the same comment about keeping display and camera timestamps on one
/// clock. Zero stays zero: the helpers use it for "no stamp", and turning it into a real date would make an unstamped
/// frame look thirty years old.
/// </remarks>
internal static class HostTime
{
    public static TimeSpan ToTimestamp(ulong nanoseconds) =>
        nanoseconds == 0 ? TimeSpan.Zero : TimeSpan.FromTicks((long)(nanoseconds / 100));
}
