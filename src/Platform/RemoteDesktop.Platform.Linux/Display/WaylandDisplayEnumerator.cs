using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Display;
using Tmds.DBus.Protocol;

namespace RemoteDesktop.Platform.Linux.Display;

/// <summary>
/// The screens a Wayland desktop has, read from the compositor.
/// </summary>
/// <remarks>
/// <para>
/// The identifiers handed out are the connector names — <c>DP-1</c>, <c>HDMI-1</c>. They are what the controller asks
/// for by name, and they are the one thing about a screen that does not move when somebody changes a resolution.
/// The bounds are physical pixels, which is what the rest of the pipeline means by a rectangle; the scale is carried
/// separately so that the pointer and the input region can be expressed in the compositor's own units.
/// </para>
/// <para>
/// <b>Why this and not the portal.</b> Reading the layout here costs nothing and needs nobody's permission, so a
/// controller can be shown the list of screens before anybody has agreed to share one. What it cannot say is which
/// screens the operator has actually granted — only a screen-cast session knows that, and its answer is narrower,
/// because the dialog has a checkbox per screen. The capturer is where that matters, and it consults the session.
/// </para>
/// <para>
/// Only Mutter is understood. On a compositor that does not answer, this reports no screens rather than inventing
/// one: a screen of unknown size cannot be captured or drawn on, and a made-up entry would put a control on the
/// controller's toolbar that does nothing when it is pressed.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class WaylandDisplayEnumerator(ILogger<WaylandDisplayEnumerator> logger) : IDisplayEnumerator, IDisposable
{
    private MutterDisplayConfig? _compositor;
    private bool _reportedFailure;

    /// <inheritdoc />
    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        try
        {
            _compositor ??= new MutterDisplayConfig();
            var monitors = _compositor.Read();

            var displays = new List<DisplayInfo>(monitors.Count);
            foreach (var monitor in monitors)
            {
                displays.Add(new DisplayInfo(
                    monitor.Connector,
                    monitor.DisplayName,
                    monitor.Geometry.PhysicalBounds,
                    monitor.Geometry.Scale,
                    monitor.Geometry.IsPrimary));
            }

            _reportedFailure = false;
            return displays;
        }
        catch (Exception error) when (error is DBusExceptionBase or InvalidOperationException or IOException)
        {
            // Not latched, because these calls come every couple of seconds and a compositor that has been restarted
            // should start working again on its own. Only the message is suppressed after the first one, so a
            // compositor that will never answer does not fill the log with the same line forever.
            if (!_reportedFailure)
            {
                _reportedFailure = true;
                LogNoLayout(logger, error.Message);
            }

            return [];
        }
    }

    public void Dispose()
    {
        _compositor?.Dispose();
        _compositor = null;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The compositor did not give up the monitor layout, so no screens can be offered. Reason: {Reason}")]
    private static partial void LogNoLayout(ILogger logger, string reason);
}
