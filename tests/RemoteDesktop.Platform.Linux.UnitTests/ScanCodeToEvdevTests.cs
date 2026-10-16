using RemoteDesktop.Platform.Linux.Input;

namespace RemoteDesktop.Platform.Linux.UnitTests;

/// <summary>
/// The key translation. The values were read out of <c>linux/input-event-codes.h</c>, and a wrong one is not a
/// cosmetic problem: it types a different letter, and it does so only for whoever happens to press that key.
/// </summary>
public sealed class ScanCodeToEvdevTests
{
    private const string LinuxOnly = "evdev key codes are a Linux thing.";

    [Theory]
    [InlineData(0x01, 1)]    // Esc
    [InlineData(0x1E, 30)]   // A
    [InlineData(0x2C, 44)]   // Z
    [InlineData(0x39, 57)]   // Space
    [InlineData(0x0E, 14)]   // Backspace
    [InlineData(0x1D, 29)]   // Left Control
    [InlineData(0x2A, 42)]   // Left Shift
    [InlineData(0x38, 56)]   // Left Alt
    [InlineData(0x3A, 58)]   // Caps Lock
    [InlineData(0x3B, 59)]   // F1
    [InlineData(0x44, 68)]   // F10
    [InlineData(0x57, 87)]   // F11
    [InlineData(0x58, 88)]   // F12
    public void Letter_and_number_keys_keep_their_position(int scanCode, int expected)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        Assert.True(ScanCodeToEvdev.TryMap(scanCode, extended: false, out var keyCode));
        Assert.Equal(expected, keyCode);
    }

    [Theory]
    [InlineData(0x01, "Esc")]
    [InlineData(0x1E, "A")]
    public void The_constants_agree_with_the_table(int scanCode, string name)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        Assert.True(ScanCodeToEvdev.TryMap(scanCode, extended: false, out var keyCode));
        _ = name;

        // The ones callers name outright, rather than passing around as numbers, have to be the same numbers.
        var expected = scanCode switch
        {
            0x01 => ScanCodeToEvdev.KeyEsc,
            0x1E => 30,
            _ => -1,
        };
        Assert.Equal(expected, keyCode);
    }

    [Fact]
    public void The_modifiers_a_lost_connection_has_to_release_are_all_present()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // InputChannelModule releases whatever it saw pressed when the channel drops, so a key that could be pressed
        // but has no mapping would be a modifier that stays stuck down.
        Assert.True(ScanCodeToEvdev.TryMap(0x1D, false, out var leftCtrl));
        Assert.True(ScanCodeToEvdev.TryMap(0x2A, false, out var leftShift));
        Assert.True(ScanCodeToEvdev.TryMap(0x38, false, out var leftAlt));
        Assert.True(ScanCodeToEvdev.TryMap(0x36, false, out var rightShift));

        Assert.Equal(ScanCodeToEvdev.KeyLeftCtrl, leftCtrl);
        Assert.Equal(ScanCodeToEvdev.KeyLeftShift, leftShift);
        Assert.Equal(ScanCodeToEvdev.KeyLeftAlt, leftAlt);
        Assert.Equal(54, rightShift);
    }

    [Fact]
    public void The_same_final_byte_means_two_different_keys_depending_on_the_prefix()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // Set 1 puts the arrow cluster, keypad Enter and Slash, and the right-hand modifiers behind a leading E0 and
        // reuses the same bytes. Sending Enter where the keypad's Enter was meant is what this test is here to catch.
        Assert.True(ScanCodeToEvdev.TryMap(0x1C, extended: false, out var enter));
        Assert.True(ScanCodeToEvdev.TryMap(0x1C, extended: true, out var keypadEnter));
        Assert.Equal(28, enter);
        Assert.Equal(96, keypadEnter);
        Assert.NotEqual(enter, keypadEnter);

        Assert.True(ScanCodeToEvdev.TryMap(0x1D, true, out var rightCtrl));
        Assert.True(ScanCodeToEvdev.TryMap(0x38, true, out var rightAlt));
        Assert.True(ScanCodeToEvdev.TryMap(0x35, true, out var keypadSlash));
        Assert.Equal(97, rightCtrl);
        Assert.Equal(100, rightAlt);
        Assert.Equal(98, keypadSlash);

        // The unextended 0x35 is the ordinary slash, and the two must not be confused either.
        Assert.True(ScanCodeToEvdev.TryMap(0x35, false, out var slash));
        Assert.Equal(53, slash);
    }

    [Theory]
    [InlineData(0x47, 102)]  // Home
    [InlineData(0x48, 103)]  // Up
    [InlineData(0x49, 104)]  // Page Up
    [InlineData(0x4B, 105)]  // Left
    [InlineData(0x4D, 106)]  // Right
    [InlineData(0x4F, 107)]  // End
    [InlineData(0x50, 108)]  // Down
    [InlineData(0x51, 109)]  // Page Down
    [InlineData(0x52, 110)]  // Insert
    [InlineData(0x53, 111)]  // Delete
    public void The_navigation_cluster_is_only_reachable_behind_the_prefix(int scanCode, int expected)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        Assert.True(ScanCodeToEvdev.TryMap(scanCode, extended: true, out var keyCode));
        Assert.Equal(expected, keyCode);

        // Without the prefix the same bytes are the keypad's own keys, so they must not answer the navigation ones.
        Assert.True(ScanCodeToEvdev.TryMap(scanCode, extended: false, out var keypad));
        Assert.NotEqual(expected, keypad);
    }

    [Theory]
    [InlineData(0x54)]  // the BIOS's SysRq, which is not a key a keyboard sends
    [InlineData(0x55)]
    [InlineData(0x59)]
    [InlineData(0x00)]
    [InlineData(0x7F)]
    public void A_scan_code_with_no_counterpart_is_reported_rather_than_guessed_at(int scanCode)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        Assert.False(ScanCodeToEvdev.TryMap(scanCode, extended: false, out _));
        Assert.False(ScanCodeToEvdev.TryMap(scanCode, extended: true, out _));
    }
}
