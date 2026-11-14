namespace RemoteDesktop.Protocol.Clipboard;

/// <summary>The largest clipboard message either end accepts, and the limits that make it up.</summary>
/// <remarks>
/// These are wire-contract limits, not feature preferences: the receiving side refuses a larger frame before it
/// allocates the payload, and the agent-forwarded host sizes its inbound frame limit from <see cref="MaxFrameBytes"/>.
/// They live here rather than on the synchronizer because the application layer composes the channel without
/// referencing the feature that drives it.
/// </remarks>
public static class ClipboardFrame
{
    public const int MaxTextBytes = 4 * 1024 * 1024;
    public const int MaxImageBytes = 8 * 1024 * 1024;

    /// <summary>A PNG plus protobuf overhead fits.</summary>
    public const int MaxFrameBytes = MaxImageBytes + (64 * 1024);
}
