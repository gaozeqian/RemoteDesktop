using System.Runtime.Versioning;
using RemoteDesktop.Platform.MacOS.Interop;

namespace RemoteDesktop.Platform.MacOS;

/// <summary>
/// Read-only look at the state the input probes measure against: where the cursor is, and whether the main display is
/// asleep. Diagnostics only — nothing here changes anything.
/// </summary>
/// <remarks>
/// The cursor position comes from an event created with no source. Such an event is a snapshot of live state rather
/// than something to post, and its location is where the cursor is. That is what lets a probe prove an injected event
/// landed, which is the one thing a registration check cannot show: without the Accessibility grant
/// <c>CGEventPost</c> returns success and does nothing at all.
/// </remarks>
[SupportedOSPlatform("macos")]
public static class MacDiagnostics
{
    /// <summary>Where the cursor is, in the global display space (points), or null when it cannot be read.</summary>
    public static (double X, double Y)? CurrentCursorLocation()
    {
        var @event = CoreGraphics.CGEventCreate(0);
        if (@event == 0)
        {
            return null;
        }

        try
        {
            var location = CoreGraphics.CGEventGetLocation(@event);
            return (location.X, location.Y);
        }
        finally
        {
            CoreFoundation.CFRelease(@event);
        }
    }

    /// <summary>
    /// Whether the main display is asleep. The window server stops tracking the cursor while it is, so a probe that
    /// posts an event and measures has to say so rather than report a move that could not be observed.
    /// </summary>
    public static bool IsDisplayAsleep() => CoreGraphics.CGDisplayIsAsleep(CoreGraphics.CGMainDisplayID()) != 0;
}
