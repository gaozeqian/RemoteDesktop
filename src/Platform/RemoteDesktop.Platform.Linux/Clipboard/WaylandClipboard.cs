using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Clipboard;

namespace RemoteDesktop.Platform.Linux.Clipboard;

/// <summary>
/// The clipboard on Wayland — which is to say, none yet.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not implemented, and it says so rather than pretending.</b> There is no way to reach the clipboard from
/// a process that is not a Wayland client, and this host is not one. The portal exists to bridge that gap, and this
/// machine's portal refuses every arrangement that was tried:
/// </para>
/// <list type="bullet">
/// <item>on a screen-cast session — <c>org.freedesktop.portal.Error: Invalid session type</c>;</item>
/// <item>on a remote-desktop session, before <c>Start</c> and after it — <c>Invalid state</c>;</item>
/// <item>on a remote-desktop session with screen sources selected as well — <c>Invalid state</c>.</item>
/// </list>
/// <para>
/// That is consistent with the portal's clipboard being meant for sandboxed applications. The fallback the port plan
/// names is owning the <c>CLIPBOARD</c> selection over XWayland with libX11 and XFixes — an X11 client can hold the
/// selection, and the compositor mirrors it to the Wayland clipboard. That is a piece of work on its own and has not
/// been attempted.
/// </para>
/// <para>
/// What matters here is that a session whose clipboard goes nowhere looks exactly like one whose clipboard works
/// until somebody copies something and pastes nothing. So the copy is refused out loud, once, and pasting yields
/// nothing — the controller can tell the difference between "empty" and "not supported" because the reason is in the
/// log and in the documentation.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class WaylandClipboard(ILogger<WaylandClipboard> logger) : IClipboard
{
    private bool _reported;

    /// <summary>
    /// Never raised, and it says so by having nothing to raise it with.
    /// </summary>
    /// <remarks>
    /// A change notification would need a connection to the compositor that this process does not have, so there is
    /// nothing to subscribe to. Declared explicitly rather than as an event with a backing field nobody invokes, which
    /// would be dead code that reads like an oversight.
    /// </remarks>
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public ClipboardContent? Get() => null;

    /// <inheritdoc />
    public void Set(ClipboardContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!_reported)
        {
            _reported = true;
            LogUnsupported(logger, content.GetType().Name);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The clipboard is not supported on this platform yet, so {Kind} was not copied. See docs/linux-port-plan.md (P5).")]
    private static partial void LogUnsupported(ILogger logger, string kind);
}
