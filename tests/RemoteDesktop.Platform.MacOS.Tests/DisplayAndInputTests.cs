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
