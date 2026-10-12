using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Platform.MacOS.Display;
using RemoteDesktop.Platform.MacOS.Input;

namespace RemoteDesktop.Platform.MacOS.Tests;

/// <summary>The real CoreGraphics display list.</summary>
public class DisplayTests
{
    [Fact]
    public void The_online_displays_are_reported_with_sane_geometry()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "CoreGraphics is macOS only.");
        var displays = new CoreGraphicsDisplays().GetDisplays();

        Assert.NotEmpty(displays);
        Assert.Single(displays, d => d.IsPrimary);

        foreach (var display in displays)
        {
            Assert.True(display.Bounds.Width > 0, $"{display.Id} has no width.");
            Assert.True(display.Bounds.Height > 0, $"{display.Id} has no height.");
            Assert.True(display.Scale >= 1.0, $"{display.Id} reports a backing scale below 1.");
            Assert.False(string.IsNullOrWhiteSpace(display.Name));
            Assert.True(uint.TryParse(display.Id, out _), "The id has to round-trip through the injector.");
        }
    }

    [Fact]
    public void The_primary_display_covers_the_origin_of_the_coordinate_space()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "CoreGraphics is macOS only.");
        var primary = new CoreGraphicsDisplays().GetDisplays().Single(d => d.IsPrimary);

        // macOS puts the origin at the main display's top-left, which is also what CGEvent takes positions in.
        Assert.Equal(0, primary.Bounds.X);
        Assert.Equal(0, primary.Bounds.Y);
    }

    [Fact]
    public void The_pixel_size_of_a_display_is_at_least_its_point_size()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "CoreGraphics is macOS only.");
        var display = new CoreGraphicsDisplays().GetDisplays().Single(d => d.IsPrimary);
        Assert.True(CoreGraphicsDisplays.TryParse(display.Id, out var id));

        var (width, height) = CoreGraphicsDisplays.PixelSize(id);

        Assert.True(width >= display.Bounds.Width, "A Retina display has more pixels than points.");
        Assert.True(height >= display.Bounds.Height);
    }
}

/// <summary>
/// The normalized-to-global pointer mapping, pinned against the one mistake that is invisible on a 1.0 display.
/// </summary>
public class PointerCoordinatesTests
{
    private static DisplayInfo Display(int x, int y, int width, int height, double scale = 1.0) =>
        new("1", "test", new PixelRect(x, y, width, height), scale, IsPrimary: x == 0 && y == 0);

    [Fact]
    public void The_top_left_of_a_display_maps_to_its_own_origin()
    {
        var (x, y) = PointerCoordinates.FromNormalized(Display(0, 0, 1440, 900), 0, 0);
        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void The_far_edge_maps_to_the_last_addressable_point_not_the_one_past_it()
    {
        var (x, y) = PointerCoordinates.FromNormalized(Display(0, 0, 1440, 900), 1, 1);

        // 1439, not 1440: a normalized 1.0 is the last coordinate the display has, and aiming one past it would put
        // the far edge of the screen permanently out of reach.
        Assert.Equal(1439, x);
        Assert.Equal(899, y);
    }

    [Fact]
    public void A_secondary_display_contributes_its_origin_as_an_offset()
    {
        // A display sitting to the right of a 1352-point-wide main display, as the machine this was written on does.
        var (x, y) = PointerCoordinates.FromNormalized(Display(1352, 0, 1920, 1080), 0, 0);

        Assert.Equal(1352, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void A_position_outside_the_unit_square_is_clamped_rather_than_extrapolated()
    {
        var display = Display(0, 0, 1440, 900);

        var below = PointerCoordinates.FromNormalized(display, -5, -5);
        var above = PointerCoordinates.FromNormalized(display, 5, 5);

        Assert.Equal(PointerCoordinates.FromNormalized(display, 0, 0), below);
        Assert.Equal(PointerCoordinates.FromNormalized(display, 1, 1), above);
    }

    [Fact]
    public void The_backing_scale_does_not_enter_the_mapping()
    {
        // The regression this exists to catch. Both displays have the same point size; one is Retina. A click at the
        // same normalized position has to land in the same place, because CGEvent counts points, not pixels — so
        // anyone "fixing" this to use the pixel size would break Retina input and nothing else.
        var plain = Display(0, 0, 1440, 900, scale: 1.0);
        var retina = Display(0, 0, 1440, 900, scale: 2.0);

        Assert.Equal(
            PointerCoordinates.FromNormalized(plain, 0.5, 0.5),
            PointerCoordinates.FromNormalized(retina, 0.5, 0.5));

        // Stated the other way round, so a mapping that multiplied by the scale cannot pass by returning a tuple of
        // zeros for both: on the Retina display the far edge is still 1439, not 2879.
        var (x, y) = PointerCoordinates.FromNormalized(retina, 1, 1);
        Assert.Equal(1439, x);
        Assert.Equal(899, y);
    }

    [Fact]
    public void The_midpoint_lands_in_the_middle_of_the_display()
    {
        var (x, y) = PointerCoordinates.FromNormalized(Display(0, 0, 1441, 901), 0.5, 0.5);

        // Half of 1440 and half of 900 — the -1 is why the centre of an odd-sized display is a whole number.
        Assert.Equal(720, x);
        Assert.Equal(450, y);
    }

    [Fact]
    public void A_null_display_is_rejected_rather_than_mapped_to_the_origin()
    {
        Assert.Throws<ArgumentNullException>(() => PointerCoordinates.FromNormalized(null!, 0.5, 0.5));
    }
}

/// <summary>The wire scan codes really do land on the right macOS keys.</summary>
public class ScanCodeMapTests
{
    [Theory]
    [InlineData(0x01, false, 0x35)] // Escape
    [InlineData(0x1E, false, 0x00)] // A
    [InlineData(0x39, false, 0x31)] // Space
    [InlineData(0x1C, false, 0x24)] // Enter
    [InlineData(0x48, true, 0x7E)]  // Arrow up, E0-prefixed
    [InlineData(0x4B, true, 0x7B)]  // Arrow left
    [InlineData(0x5B, true, 0x37)]  // Left Windows key → Command
    public void A_scan_code_maps_to_the_key_in_the_same_position(int scanCode, bool extended, int expected)
    {
        Assert.True(ScanCodeToMacKey.TryGet(scanCode, extended, out var macKey));
        Assert.Equal((ushort)expected, macKey);
    }

    [Fact]
    public void Keys_that_macOS_has_no_place_for_are_dropped_rather_than_guessed()
    {
        // PrintScreen (E0 37) and ScrollLock (46) have no macOS equivalent.
        Assert.False(ScanCodeToMacKey.TryGet(0x37, true, out _));
        Assert.False(ScanCodeToMacKey.TryGet(0x46, false, out _));
    }

    [Fact]
    public void The_modifiers_are_recognized_as_such()
    {
        Assert.True(ScanCodeToMacKey.TryGet(0x1D, false, out var control));
        Assert.NotEqual(0UL, ScanCodeToMacKey.ModifierFlag(control));

        Assert.True(ScanCodeToMacKey.TryGet(0x2A, false, out var shift));
        Assert.NotEqual(0UL, ScanCodeToMacKey.ModifierFlag(shift));

        Assert.True(ScanCodeToMacKey.TryGet(0x1E, false, out var letterA));
        Assert.Equal(0UL, ScanCodeToMacKey.ModifierFlag(letterA));
    }
}
