using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Platform.Linux.XdgPortal;

namespace RemoteDesktop.Platform.Linux.Display;

/// <summary>
/// Decides which of the screens the desktop granted is the one a controller asked for — and what that screen really is.
/// </summary>
/// <remarks>
/// <para>
/// Two lists have to be reconciled. The compositor describes each monitor in device pixels, positioned by
/// <c>round(logical × its own scale)</c>; the portal describes a granted stream in the compositor's layout units. They
/// are the same rectangles in different units, so the comparison is done in logical space, where both sides mean the
/// same thing by a number.
/// </para>
/// <para>
/// <b>A monitor that was not shared cannot be captured.</b> The screen-cast dialog decides which streams exist, and
/// nothing in the protocol asks for a different one afterwards — the operator has to re-share. What this type can do is
/// refuse loudly instead of streaming a different screen under the requested name, and, when there is only one stream,
/// say plainly which screen it is: its size is what the stream will have, and pinning the capture format to the
/// requested screen's size is exactly how a shared 1920×1080 monitor used to reach a copier that expected 2560×1440.
/// </para>
/// </remarks>
internal static class PortalDisplayMatch
{
    /// <summary>
    /// Logical pixels of slack. Both sides round independently and a fractional scale leaves one or two.
    /// </summary>
    private const int Tolerance = 2;

    /// <summary>
    /// Picks the granted stream for <paramref name="requested"/> and pairs it with the desktop's description of that
    /// same screen, whose bounds are the size the capture format has to use.
    /// </summary>
    /// <param name="requested">The display the controller asked for, out of the compositor's list.</param>
    /// <param name="granted">The streams the portal started, in compositor coordinates.</param>
    /// <param name="desktop">The compositor's monitors, in device pixels.</param>
    /// <exception cref="InvalidOperationException">
    /// Several screens were granted and none of them is the one asked for.
    /// </exception>
    public static (SharedScreen Screen, DisplayInfo Display) Resolve(
        DisplayInfo requested,
        IReadOnlyList<SharedScreen> granted,
        IReadOnlyList<DisplayInfo> desktop)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(granted);
        ArgumentNullException.ThrowIfNull(desktop);

        foreach (var screen in granted)
        {
            if (Is(screen, requested))
            {
                // The usual case: the operator shared the screen that was asked for, so the request's own bounds are
                // the screen's real bounds.
                return (screen, requested);
            }
        }

        if (granted.Count == 1)
        {
            // One screen is being shared, so it is the screen — whatever the controller picked. Capturing it at the
            // requested screen's size would pin the format to the wrong dimensions and produce frames the copier
            // rejects; its own size is the only one that works.
            var only = granted[0];
            return (only, Find(only, desktop) ?? PhysicalFrom(requested, only));
        }

        throw new InvalidOperationException(
            $"The desktop is sharing {Describe(granted)}, not '{requested.Id}'. Share that screen in the desktop's dialog, " +
            "or choose one that is being shared.");
    }

    /// <summary>Whether a granted stream is the screen a display describes, in the compositor's units.</summary>
    public static bool Is(SharedScreen screen, DisplayInfo display)
    {
        ArgumentNullException.ThrowIfNull(display);

        var scale = display.Scale <= 0 ? 1.0 : display.Scale;
        var logical = Logical(display, scale);

        return Near(screen.X, logical.X)
            && Near(screen.Y, logical.Y)
            && Near(screen.Width, logical.Width)
            && Near(screen.Height, logical.Height);
    }

    /// <summary>
    /// The compositor's own description of a granted stream, or <c>null</c> when none of its monitors matches it or two
    /// of them do (identical monitors at different positions are told apart by the position, so two matches means the
    /// layout itself is ambiguous).
    /// </summary>
    public static DisplayInfo? Find(SharedScreen screen, IReadOnlyList<DisplayInfo> desktop)
    {
        ArgumentNullException.ThrowIfNull(desktop);

        DisplayInfo? found = null;
        foreach (var display in desktop)
        {
            if (!Is(screen, display))
            {
                continue;
            }

            if (found is not null)
            {
                return null;
            }

            found = display;
        }

        return found;
    }

    /// <summary>
    /// The granted stream in device pixels, using the one scale this method can know: the requested display's. Only
    /// reached when the compositor could not name the screen, which is the case where a small scale error beats
    /// refusing to share at all.
    /// </summary>
    public static DisplayInfo PhysicalFrom(DisplayInfo requested, SharedScreen screen)
    {
        ArgumentNullException.ThrowIfNull(requested);

        var scale = requested.Scale <= 0 ? 1.0 : requested.Scale;
        return requested with
        {
            Bounds = new PixelRect(
                (int)Math.Round(screen.X * scale),
                (int)Math.Round(screen.Y * scale),
                Math.Max(1, (int)Math.Round(screen.Width * scale)),
                Math.Max(1, (int)Math.Round(screen.Height * scale))),
        };
    }

    private static PixelRect Logical(DisplayInfo display, double scale) => new(
        (int)Math.Round(display.Bounds.X / scale),
        (int)Math.Round(display.Bounds.Y / scale),
        (int)Math.Round(display.Bounds.Width / scale),
        (int)Math.Round(display.Bounds.Height / scale));

    private static bool Near(int left, int right) => Math.Abs(left - right) <= Tolerance;

    private static string Describe(IReadOnlyList<SharedScreen> screens) =>
        string.Join(", ", screens.Select(screen => $"{screen.Width}×{screen.Height}"));
}
