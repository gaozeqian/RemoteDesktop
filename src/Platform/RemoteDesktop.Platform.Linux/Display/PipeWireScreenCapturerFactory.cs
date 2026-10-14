using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Platform.Linux.XdgPortal;

namespace RemoteDesktop.Platform.Linux.Display;

/// <summary>
/// Hands out a capturer per screen the desktop has granted.
/// </summary>
/// <remarks>
/// <para>
/// The permission itself belongs to <see cref="PortalGrant"/>, which is shared with the input side. What is left
/// here is the part that is about screens: deciding which of the granted streams is the screen that was asked for.
/// </para>
/// <para>
/// The two lists do not use the same units. The compositor describes a screen in device pixels, which is what
/// <see cref="DisplayInfo.Bounds"/> carries; the portal describes the same screen in the units the compositor lays
/// windows out in, which are scaled. The scale is therefore applied to the portal's position before the two are
/// compared — without it, a screen at any scale other than one would never be found, and the ordinary single-screen
/// desktop would still work by falling through to the only stream there is.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class PipeWireScreenCapturerFactory(
    PortalGrant grant,
    ILoggerFactory loggerFactory,
    ILogger<PipeWireScreenCapturerFactory> logger) : IScreenCapturerFactory
{
    public IScreenCapturer Create(DisplayInfo display)
    {
        ArgumentNullException.ThrowIfNull(display);

        var screens = grant.EnsureScreens();
        if (screens.Count == 0)
        {
            throw new InvalidOperationException("The desktop granted no screens to share.");
        }

        var screen = Match(display, screens);
        var descriptor = grant.DuplicateRemoteDescriptor();

        LogStarting(logger, display.Id, screen.NodeId, display.Bounds.Width, display.Bounds.Height);

        return new PipeWireScreenCapturer(
            descriptor,
            screen.NodeId,
            display,
            display.Bounds.Width,
            display.Bounds.Height,
            loggerFactory.CreateLogger<PipeWireScreenCapturer>());
    }

    private static SharedScreen Match(DisplayInfo display, IReadOnlyList<SharedScreen> screens)
    {
        var scale = display.Scale <= 0 ? 1.0 : display.Scale;

        foreach (var screen in screens)
        {
            var x = (int)Math.Round(screen.X * scale);
            var y = (int)Math.Round(screen.Y * scale);

            if (x == display.Bounds.X && y == display.Bounds.Y)
            {
                return screen;
            }
        }

        if (screens.Count == 1)
        {
            // One screen was granted and one was asked for; they are the same screen whatever the arithmetic says.
            // This is the ordinary case, and the alternative is refusing to capture over a rounding difference.
            return screens[0];
        }

        throw new InvalidOperationException(
            $"The desktop granted {screens.Count} screens and none of them is '{display.Id}'.");
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Capturing '{Display}' from PipeWire node {Node} at {Width}x{Height}.")]
    private static partial void LogStarting(ILogger logger, string display, uint node, int width, int height);
}
