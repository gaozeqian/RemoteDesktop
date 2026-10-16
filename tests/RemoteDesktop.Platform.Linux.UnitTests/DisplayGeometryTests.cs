using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Platform.Linux.Display;

namespace RemoteDesktop.Platform.Linux.UnitTests;

/// <summary>
/// The two coordinate systems a Wayland desktop has, and the conversion between them.
/// </summary>
/// <remarks>
/// The numbers are the ones measured on the machine this was written on: a 2560x1440 monitor running at scale 4/3,
/// which the compositor lays out as a 1920x1080 rectangle. They are used as they are rather than invented, because
/// invented numbers tend to be round ones, and round ones hide exactly the rounding this code has to get right.
/// </remarks>
public sealed class DisplayGeometryTests
{
    private const double Scale = 4.0 / 3.0;

    /// <summary>The monitor this was measured on: 2560x1440 of pixels shown as 1920x1080 of layout.</summary>
    private static CompositorMonitor Measured => new(LogicalX: 0, LogicalY: 0, Scale: Scale, PhysicalWidth: 2560, PhysicalHeight: 1440, IsPrimary: true);

    [Fact]
    public void A_scaled_monitor_keeps_its_pixels_and_reports_its_scale()
    {
        var monitor = Measured;

        // DisplayInfo.Bounds is device pixels, which is what the capturing and encoding paths count in.
        Assert.Equal(new PixelRect(0, 0, 2560, 1440), monitor.PhysicalBounds);

        // And Scale stops being 1.0 here, which is the first time it ever has: every Windows display reported 1.0.
        Assert.Equal(Scale, monitor.Scale, precision: 5);
    }

    [Fact]
    public void The_compositor_rectangle_is_the_pixels_divided_by_the_scale()
    {
        var monitor = Measured;

        Assert.Equal(1920, monitor.LogicalWidth);
        Assert.Equal(1080, monitor.LogicalHeight);
        Assert.Equal(new PixelRect(0, 0, 1920, 1080), monitor.LogicalBounds);
    }

    [Fact]
    public void A_monitor_placed_to_the_right_is_moved_by_its_own_scale()
    {
        // The position the compositor gives is in layout units, so a monitor at layout x=1920 on a 4/3 desktop
        // starts at pixel 2560 — not at 1920. Getting this wrong puts the pointer one monitor's width out.
        var second = new CompositorMonitor(LogicalX: 1920, LogicalY: 0, Scale: Scale, PhysicalWidth: 2560, PhysicalHeight: 1440, IsPrimary: false);

        Assert.Equal(new PixelRect(2560, 0, 2560, 1440), second.PhysicalBounds);
        Assert.Equal(new PixelRect(1920, 0, 1920, 1080), second.LogicalBounds);
    }

    [Fact]
    public void Mixed_scales_do_not_share_one_factor()
    {
        // A 4/3 monitor beside a 1:1 one. Its layout position is 1920 because that is where the first one ends in
        // layout units, but in pixels the second monitor starts at 2560 — and its own pixels are its own layout units.
        var scaled = Measured;
        var plain = new CompositorMonitor(LogicalX: 1920, LogicalY: 0, Scale: 1.0, PhysicalWidth: 1920, PhysicalHeight: 1080, IsPrimary: false);

        var union = DisplayGeometry.LogicalUnion([scaled, plain]);

        Assert.Equal(new PixelRect(0, 0, 3840, 1080), union);
        // The second monitor is 1920 wide in both systems only because its scale is 1; a single global factor would
        // have made it 1440 or 2560 depending on which monitor the factor came from.
        Assert.Equal(1920, plain.LogicalWidth);
        Assert.Equal(new PixelRect(1920, 0, 1920, 1080), plain.LogicalBounds);
    }

    [Fact]
    public void The_region_covers_every_monitor_not_just_the_first()
    {
        var left = new CompositorMonitor(-1920, 0, 1.0, 1920, 1080, IsPrimary: false);
        var right = new CompositorMonitor(0, -120, 1.0, 2560, 1440, IsPrimary: true);

        var union = DisplayGeometry.LogicalUnion([left, right]);

        // A region that only covered one monitor would make the compositor refuse a pointer move beyond it rather
        // than clamp it, which reads as a pointer that stops at an invisible wall. The taller monitor is the one 120
        // units above the origin, so the desktop runs from -120 to its bottom edge at 1320.
        Assert.Equal(new PixelRect(-1920, -120, 4480, 1440), union);
    }

    [Fact]
    public void Taking_the_bounds_of_nothing_says_so()
    {
        Assert.Throws<ArgumentException>(() => DisplayGeometry.LogicalUnion([]));
    }

    [Fact]
    public void A_position_on_a_monitor_becomes_a_position_on_the_desktop()
    {
        var monitor = Measured;

        // Normalized against this one monitor: its middle is (0.5, 0.5).
        Assert.Equal((0.5, 0.5), DisplayGeometry.ToDesktopNormalized(monitor, monitor.LogicalBounds, 0.5, 0.5));
        Assert.Equal((0.0, 0.0), DisplayGeometry.ToDesktopNormalized(monitor, monitor.LogicalBounds, 0.0, 0.0));
        Assert.Equal((1.0, 1.0), DisplayGeometry.ToDesktopNormalized(monitor, monitor.LogicalBounds, 1.0, 1.0));
    }

    [Fact]
    public void A_position_on_the_second_monitor_lands_past_the_middle_of_the_desktop()
    {
        var first = Measured;
        var second = new CompositorMonitor(LogicalX: 1920, LogicalY: 0, Scale: 1.0, PhysicalWidth: 1920, PhysicalHeight: 1080, IsPrimary: false);
        var union = DisplayGeometry.LogicalUnion([first, second]);

        // The wire sends a position normalized against the display it is on, so the same 0.5 means the middle of the
        // second monitor; across the whole desktop that is three quarters of the way along.
        var (x, y) = DisplayGeometry.ToDesktopNormalized(second, union, 0.5, 0.5);

        Assert.Equal(2880.0 / 3840.0, x, precision: 5);
        Assert.Equal(540.0 / 1080.0, y, precision: 5);
    }

    [Fact]
    public void A_monitor_above_or_left_of_the_origin_gives_a_negative_position_on_the_desktop()
    {
        var above = new CompositorMonitor(LogicalX: 0, LogicalY: -1080, Scale: 1.0, PhysicalWidth: 1920, PhysicalHeight: 1080, IsPrimary: true);
        var union = DisplayGeometry.LogicalUnion([above, new CompositorMonitor(0, 0, 1.0, 1920, 1080, IsPrimary: false)]);

        var (x, y) = DisplayGeometry.ToDesktopNormalized(above, union, 0.5, 0.5);

        Assert.Equal(0.5, x, precision: 5);
        // The top of the upper monitor is the top of the desktop, so its middle is a quarter of the way down.
        Assert.Equal(0.25, y, precision: 5);
    }
}
