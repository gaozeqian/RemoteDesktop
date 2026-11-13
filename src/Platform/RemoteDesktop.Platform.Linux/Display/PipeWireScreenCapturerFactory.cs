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
/// Which stream is which is decided in <see cref="PortalDisplayMatch"/>, in the compositor's own units. What is left
/// here is the consequence: the capture format is pinned to the shared screen's real size, and the display that goes
/// on the wire is that screen rather than the one that was asked for. A desktop that shares one monitor while the
/// controller picks another therefore shows the shared one at its own size instead of answering a format request with
/// frames nobody can copy.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed partial class PipeWireScreenCapturerFactory(
    PortalGrant grant,
    IDisplayEnumerator desktop,
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

        // Which granted stream is this screen, and what the desktop says that screen's size is. The second half is
        // what the capture format is pinned to: a stream of a monitor that was not asked for still has to be captured
        // at its own size, or the frames arrive with a stride the copier rejects.
        var (screen, actual) = PortalDisplayMatch.Resolve(display, screens, desktop.GetDisplays());

        if (!string.Equals(actual.Id, display.Id, StringComparison.Ordinal))
        {
            // Said once per switch, because "why am I looking at the other monitor" is the question this answers.
            LogSubstituted(logger, display.Id, actual.Id, actual.Bounds.Width, actual.Bounds.Height);
        }

        var descriptor = grant.DuplicateRemoteDescriptor();
        LogStarting(logger, actual.Id, screen.NodeId, actual.Bounds.Width, actual.Bounds.Height);

        return new PipeWireScreenCapturer(
            descriptor,
            screen.NodeId,
            actual,
            actual.Bounds.Width,
            actual.Bounds.Height,
            loggerFactory.CreateLogger<PipeWireScreenCapturer>());
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Capturing '{Display}' from PipeWire node {Node} at {Width}x{Height}.")]
    private static partial void LogStarting(ILogger logger, string display, uint node, int width, int height);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The desktop is sharing '{Actual}' ({Width}x{Height}), not the requested '{Requested}'; the stream is capped at the shared screen's size and announced as that screen.")]
    private static partial void LogSubstituted(ILogger logger, string requested, string actual, int width, int height);
}
