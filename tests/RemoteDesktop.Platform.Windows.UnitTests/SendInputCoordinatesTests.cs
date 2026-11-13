using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Platform.Windows.Input;

namespace RemoteDesktop.Platform.Windows.UnitTests;

/// <summary>
/// The two decisions <c>SendInputInjector</c> makes before the interop call. They are tested here because they are
/// pure: a display name that no longer matches must land on the primary display rather than wherever the cursor was,
/// and a normalized point must reach the same edge of the virtual desktop that the event named.
/// </summary>
public sealed class SendInputCoordinatesTests
{
    [Fact]
    public void The_display_named_by_the_event_wins_over_the_primary_one()
    {
        var primary = Display("primary", 0, 0, 1920, 1080, primary: true);
        var secondary = Display("secondary", 1920, 0, 1920, 1080);

        Assert.Same(secondary, SendInputCoordinates.SelectDisplay([primary, secondary], "secondary"));
    }

    [Fact]
    public void A_display_name_that_no_longer_exists_falls_back_to_the_primary_display()
    {
        var primary = Display("primary", 0, 0, 1920, 1080, primary: true);
        var secondary = Display("secondary", 1920, 0, 1920, 1080);

        Assert.Same(primary, SendInputCoordinates.SelectDisplay([secondary, primary], "disconnected"));
    }

    [Fact]
    public void Without_a_primary_marker_the_first_display_is_used()
    {
        var first = Display("first", 0, 0, 1920, 1080);
        var second = Display("second", 1920, 0, 1920, 1080);

        Assert.Same(first, SendInputCoordinates.SelectDisplay([first, second], "disconnected"));
    }

    [Fact]
    public void An_empty_display_list_is_refused_rather_than_inventing_a_position()
    {
        Assert.Throws<InvalidOperationException>(
            () => SendInputCoordinates.SelectDisplay([], "anything"));
    }

    [Fact]
    public void The_ends_of_a_display_map_to_the_ends_of_the_absolute_range()
    {
        var display = Display("only", 0, 0, 101, 101);
        var screen = new VirtualScreen(0, 0, 101, 101);

        Assert.Equal((0, 0), SendInputCoordinates.ToVirtualDesktop(0, 0, display, screen));
        Assert.Equal((65535, 65535), SendInputCoordinates.ToVirtualDesktop(1, 1, display, screen));
    }

    [Fact]
    public void A_position_outside_the_display_is_clamped_instead_of_throwing()
    {
        var display = Display("only", 0, 0, 101, 101);
        var screen = new VirtualScreen(0, 0, 101, 101);

        Assert.Equal((0, 0), SendInputCoordinates.ToVirtualDesktop(-1, -1, display, screen));
        Assert.Equal((65535, 65535), SendInputCoordinates.ToVirtualDesktop(2, 2, display, screen));
    }

    [Fact]
    public void A_display_offset_is_kept_relative_to_the_virtual_desktop_origin()
    {
        var display = Display("right", 10, 20, 101, 101);
        var screen = new VirtualScreen(0, 0, 121, 141);

        // The display's top-left sits 10 px into a 120 px-wide virtual desktop and 20 px into a 140 px-high one.
        Assert.Equal(
            ((int)Math.Round(10 * 65535.0 / 120), (int)Math.Round(20 * 65535.0 / 140)),
            SendInputCoordinates.ToVirtualDesktop(0, 0, display, screen));
    }

    [Fact]
    public void A_monitor_left_of_the_origin_still_maps_to_the_start_of_the_range()
    {
        var display = Display("left", -1920, 0, 1920, 1080);
        var screen = new VirtualScreen(-1920, 0, 3840, 1080);

        Assert.Equal(0, SendInputCoordinates.ToVirtualDesktop(0, 0, display, screen).Dx);
    }

    private static DisplayInfo Display(string id, int x, int y, int width, int height, bool primary = false) =>
        new(id, id, new PixelRect(x, y, width, height), Scale: 1.0, primary);
}
