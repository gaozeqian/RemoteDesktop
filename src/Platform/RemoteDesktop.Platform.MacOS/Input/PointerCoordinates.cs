using RemoteDesktop.Abstractions.Display;

namespace RemoteDesktop.Platform.MacOS.Input;

/// <summary>
/// Turns a pointer position from the wire into a point in the global display coordinate space.
/// </summary>
/// <remarks>
/// <para>
/// Three coordinate spaces meet here, and only the last of them is what <c>CGEvent</c> accepts:
/// </para>
/// <list type="number">
/// <item>the wire carries a position normalized to 0..1 within one display. That is what keeps the two ends from
/// having to agree about pixels at all, and it is why the client can be a Windows box driving a Retina Mac;</item>
/// <item>a captured frame is measured in the display's <em>pixels</em>, which on a Retina Mac is the point size
/// multiplied by the backing scale (2.0 on the main display of the machine this was written on);</item>
/// <item><c>CGEvent</c> takes positions in the global display space, which is in <em>points</em>, origin at the
/// top-left of the main display, with every other display's own origin acting as an offset into it.</item>
/// </list>
/// <para>
/// The scale factor therefore does not appear anywhere below, and its absence is the point. Multiplying by the pixel
/// size instead of the point size would put every click on a Retina display at twice its intended distance from the
/// display's origin — correct at the top-left corner, wrong everywhere else, and invisible on a 1.0 display. The
/// mapping lives in its own type so that rule can be pinned by a test rather than restated in a comment.
/// </para>
/// <para>
/// The subtraction of one pixel of travel is deliberate too: a normalized 1.0 means "the last addressable
/// coordinate", which is <c>Width - 1</c>, not <c>Width</c>. Landing on <c>Width</c> would be one past the display
/// and would be clamped away by the system, making the far edge of the screen unreachable.
/// </para>
/// </remarks>
public static class PointerCoordinates
{
    /// <param name="display">The display the normalized position is relative to.</param>
    /// <param name="x">Horizontal position within that display, 0 at its left edge and 1 at its right edge.</param>
    /// <param name="y">Vertical position within that display, 0 at its top edge and 1 at its bottom edge.</param>
    /// <returns>A point in the global display space, in points.</returns>
    public static (double X, double Y) FromNormalized(DisplayInfo display, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(display);

        var bounds = display.Bounds;
        return (
            bounds.X + (Math.Clamp(x, 0, 1) * (bounds.Width - 1)),
            bounds.Y + (Math.Clamp(y, 0, 1) * (bounds.Height - 1)));
    }
}
