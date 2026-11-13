using RemoteDesktop.Abstractions.Display;

namespace RemoteDesktop.Platform.Windows.Input;

/// <summary>The virtual-desktop rectangle the absolute mouse coordinates are relative to.</summary>
/// <param name="Left">SM_XVIRTUALSCREEN, which can be negative when a monitor sits left of the primary one.</param>
/// <param name="Top">SM_YVIRTUALSCREEN, which can be negative for the same reason.</param>
/// <param name="Width">SM_CXVIRTUALSCREEN; two is the smallest divisor the mapping can use.</param>
/// <param name="Height">SM_CYVIRTUALSCREEN; two is the smallest divisor the mapping can use.</param>
internal readonly record struct VirtualScreen(int Left, int Top, int Width, int Height)
{
    public int SafeWidth => Math.Max(2, Width);

    public int SafeHeight => Math.Max(2, Height);
}

/// <summary>
/// The two rules <see cref="SendInputInjector"/> applies before calling <c>SendInput</c>: which display a pointer
/// event belongs to, and where that display's normalized position falls in the virtual desktop's 0..65535 space.
/// </summary>
/// <remarks>
/// Separated from the injector because both are pure decisions. The injector's remaining work — the display cache and
/// the interop structs — is what a test cannot run without a desktop; these two are the parts whose off-by-one would
/// move somebody else's cursor.
/// </remarks>
internal static class SendInputCoordinates
{
    /// <summary>
    /// The display named by the event, or the primary one when the name is unknown, or the first one when the primary
    /// is not marked. A pointer event that cannot name a display still has to land somewhere sensible.
    /// </summary>
    public static DisplayInfo SelectDisplay(IReadOnlyList<DisplayInfo> displays, string displayId)
    {
        ArgumentNullException.ThrowIfNull(displays);

        DisplayInfo? primary = null;
        foreach (var display in displays)
        {
            if (string.Equals(display.Id, displayId, StringComparison.Ordinal))
            {
                return display;
            }

            primary ??= display.IsPrimary ? display : null;
        }

        return primary ?? (displays.Count > 0
            ? displays[0]
            : throw new InvalidOperationException("No display to inject input into."));
    }

    /// <summary>
    /// Converts a position normalized to one display into the absolute coordinate space <c>SendInput</c> expects,
    /// which spans the whole virtual desktop. Both inputs are clamped, so a malformed event cannot throw here.
    /// </summary>
    public static (int Dx, int Dy) ToVirtualDesktop(
        double x,
        double y,
        DisplayInfo display,
        VirtualScreen screen)
    {
        ArgumentNullException.ThrowIfNull(display);

        var px = display.Bounds.X + (Math.Clamp(x, 0, 1) * (display.Bounds.Width - 1));
        var py = display.Bounds.Y + (Math.Clamp(y, 0, 1) * (display.Bounds.Height - 1));
        return (
            (int)Math.Round((px - screen.Left) * 65535.0 / (screen.SafeWidth - 1)),
            (int)Math.Round((py - screen.Top) * 65535.0 / (screen.SafeHeight - 1)));
    }
}
