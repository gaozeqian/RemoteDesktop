using RemoteDesktop.Abstractions.Display;

namespace RemoteDesktop.Platform.Linux.Display;

/// <summary>
/// One monitor as the compositor describes it.
/// </summary>
/// <param name="LogicalX">Left edge in compositor coordinates, which are scaled and not pixels.</param>
/// <param name="LogicalY">Top edge in compositor coordinates.</param>
/// <param name="Scale">How many device pixels make one compositor pixel; 1.33333 on a HiDPI screen at 4/3.</param>
/// <param name="PhysicalWidth">Width of the monitor's current mode, in device pixels.</param>
/// <param name="PhysicalHeight">Height of the monitor's current mode, in device pixels.</param>
/// <param name="IsPrimary">Whether the compositor treats this as the primary monitor.</param>
/// <remarks>
/// Both coordinate systems are needed and neither converts to the other by a constant. The compositor works in scaled
/// units because that is what it lays windows out in, and a monitor's position there is scaled by <em>its own</em>
/// scale — so on a mixed-DPI desktop the gap between two monitors is not the same number of device pixels
/// everywhere. Everything that talks to the compositor (the input region, matching a portal stream to a monitor)
/// wants the logical one; everything that carries pixels (capture, encoding, the pointer position on the wire)
/// wants the physical one. Mixing them up puts the pointer at the wrong place on exactly the desktops that are
/// hardest to notice it on.
/// </remarks>
public readonly record struct CompositorMonitor(
    int LogicalX,
    int LogicalY,
    double Scale,
    int PhysicalWidth,
    int PhysicalHeight,
    bool IsPrimary)
{
    /// <summary>Width in compositor coordinates, which is what the monitor occupies on the logical desktop.</summary>
    public int LogicalWidth => (int)Math.Round(PhysicalWidth / Scale);

    /// <summary>Height in compositor coordinates.</summary>
    public int LogicalHeight => (int)Math.Round(PhysicalHeight / Scale);

    /// <summary>Where the monitor sits in device pixels, which is what <see cref="DisplayInfo.Bounds"/> means.</summary>
    public PixelRect PhysicalBounds =>
        new((int)Math.Round(LogicalX * Scale), (int)Math.Round(LogicalY * Scale), PhysicalWidth, PhysicalHeight);

    /// <summary>Where the monitor sits in compositor coordinates.</summary>
    public PixelRect LogicalBounds => new(LogicalX, LogicalY, LogicalWidth, LogicalHeight);
}

/// <summary>
/// Conversions between the two coordinate systems above, kept in one place so that the arithmetic is done once and
/// can be checked against numbers measured on a real desktop.
/// </summary>
internal static class DisplayGeometry
{
    /// <summary>
    /// The rectangle enclosing every monitor, in compositor coordinates.
    /// </summary>
    /// <remarks>
    /// This is the region an absolute pointer is expressed against, so it has to cover the whole desktop rather than
    /// one monitor — an injected move outside the region is refused by the compositor rather than clamped, which
    /// would show up as a pointer that stops at the edge of whichever monitor it started on.
    /// </remarks>
    /// <exception cref="ArgumentException">No monitors were given.</exception>
    public static PixelRect LogicalUnion(IEnumerable<CompositorMonitor> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;

        foreach (var monitor in monitors)
        {
            var bounds = monitor.LogicalBounds;
            left = Math.Min(left, bounds.X);
            top = Math.Min(top, bounds.Y);
            right = Math.Max(right, bounds.X + bounds.Width);
            bottom = Math.Max(bottom, bounds.Y + bounds.Height);
        }

        if (right <= left || bottom <= top)
        {
            throw new ArgumentException("No monitors to take the bounds of.", nameof(monitors));
        }

        return new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Turns a position on one monitor into the coordinates an absolute pointer is sent in: 0..1 across the whole
    /// desktop, not across the monitor.
    /// </summary>
    /// <param name="monitor">The monitor the position is on.</param>
    /// <param name="region">The rectangle enclosing every monitor, from <see cref="LogicalUnion"/>.</param>
    /// <param name="x">How far across the monitor the pointer is, from 0 at its left edge to 1 at its right.</param>
    /// <param name="y">How far down the monitor the pointer is.</param>
    /// <remarks>
    /// The wire protocol normalizes a pointer position against the display it is on, which is convenient for a
    /// controller that knows the display list and useless to a compositor that only knows the desktop. This is the
    /// conversion between the two.
    /// </remarks>
    public static (double X, double Y) ToDesktopNormalized(CompositorMonitor monitor, PixelRect region, double x, double y)
    {
        var local = monitor.LogicalBounds;
        var onDesktopX = local.X + (x * local.Width);
        var onDesktopY = local.Y + (y * local.Height);

        return ((onDesktopX - region.X) / region.Width, (onDesktopY - region.Y) / region.Height);
    }
}
