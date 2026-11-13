using System.Collections.Frozen;
using System.Runtime.Versioning;

namespace RemoteDesktop.Platform.Linux.Input;

/// <summary>
/// Translates the PC scan codes the wire protocol carries into the key codes Linux input devices use.
/// </summary>
/// <remarks>
/// <para>
/// The two are different numbering systems and there is no formula between them, so this is a table. The controller
/// sends <em>scan code set 1</em> — the numbers a PC keyboard's own controller produced, which is what the Windows
/// side passes straight to <c>SendInput</c> — while evdev numbers keys by position on the keyboard
/// (<c>KEY_A = 30</c>, <c>KEY_ESC = 1</c>). The values here are copied from
/// <c>linux/input-event-codes.h</c>, which is the authority for the second half.
/// </para>
/// <para>
/// A handful of codes only exist to make the scan code unambiguous: set 1 puts the arrow cluster, the keypad's
/// Enter and Slash, right Control and right Alt, and the two Windows keys behind a leading <c>E0</c>, and reuses the
/// same final byte for a different key without it. <c>0x1C</c> is Enter on a keyboard and the keypad's Enter behind
/// <c>E0</c>; sending one for the other is exactly the kind of mistake this table's key exists to prevent. That is
/// what <c>IsExtended</c> disambiguates.
/// </para>
/// <para>
/// Codes with no counterpart are left out rather than guessed at — the BIOS's SysRq, and the <c>E1</c> sequences
/// Pause is made of. <see cref="TryMap"/> reports those instead of quietly sending some other key.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal static class ScanCodeToEvdev
{
    /// <summary>Numbers keys by position, the way evdev does; the values are from <c>linux/input-event-codes.h</c>.</summary>
    public const int KeyEsc = 1;
    public const int KeyEnter = 28;
    public const int KeyLeftCtrl = 29;
    public const int KeyLeftShift = 42;
    public const int KeyLeftAlt = 56;
    public const int KeyCapsLock = 58;
    public const int KeyLeftMeta = 125;

    private static readonly FrozenDictionary<(int ScanCode, bool Extended), int> Keys = Build();

    /// <summary>Looks up the key code for a scan code.</summary>
    /// <param name="scanCode">The set-1 scan code, without the <c>E0</c> prefix.</param>
    /// <param name="extended">Whether the controller saw the <c>E0</c> prefix in front of it.</param>
    /// <param name="keyCode">The evdev key code, when there is one.</param>
    /// <returns><c>false</c> when this scan code has no counterpart here.</returns>
    public static bool TryMap(int scanCode, bool extended, out int keyCode) =>
        Keys.TryGetValue((scanCode, extended), out keyCode);

    private static FrozenDictionary<(int ScanCode, bool Extended), int> Build()
    {
        // Written as the unextended sequence first, because that is the order the numbers run in and reading them
        // side by side is how a mistyped row gets noticed.
        (int ScanCode, bool Extended, int KeyCode)[] rows =
        [
            (0x01, false, 1),    // Esc
            (0x02, false, 2),    // 1
            (0x03, false, 3),    // 2
            (0x04, false, 4),    // 3
            (0x05, false, 5),    // 4
            (0x06, false, 6),    // 5
            (0x07, false, 7),    // 6
            (0x08, false, 8),    // 7
            (0x09, false, 9),    // 8
            (0x0A, false, 10),   // 9
            (0x0B, false, 11),   // 0
            (0x0C, false, 12),   // -
            (0x0D, false, 13),   // =
            (0x0E, false, 14),   // Backspace
            (0x0F, false, 15),   // Tab
            (0x10, false, 16),   // Q
            (0x11, false, 17),   // W
            (0x12, false, 18),   // E
            (0x13, false, 19),   // R
            (0x14, false, 20),   // T
            (0x15, false, 21),   // Y
            (0x16, false, 22),   // U
            (0x17, false, 23),   // I
            (0x18, false, 24),   // O
            (0x19, false, 25),   // P
            (0x1A, false, 26),   // [
            (0x1B, false, 27),   // ]
            (0x1C, false, 28),   // Enter
            (0x1D, false, 29),   // Left Control
            (0x1E, false, 30),   // A
            (0x1F, false, 31),   // S
            (0x20, false, 32),   // D
            (0x21, false, 33),   // F
            (0x22, false, 34),   // G
            (0x23, false, 35),   // H
            (0x24, false, 36),   // J
            (0x25, false, 37),   // K
            (0x26, false, 38),   // L
            (0x27, false, 39),   // ;
            (0x28, false, 40),   // '
            (0x29, false, 41),   // `
            (0x2A, false, 42),   // Left Shift
            (0x2B, false, 43),   // \
            (0x2C, false, 44),   // Z
            (0x2D, false, 45),   // X
            (0x2E, false, 46),   // C
            (0x2F, false, 47),   // V
            (0x30, false, 48),   // B
            (0x31, false, 49),   // N
            (0x32, false, 50),   // M
            (0x33, false, 51),   // ,
            (0x34, false, 52),   // .
            (0x35, false, 53),   // /
            (0x36, false, 54),   // Right Shift
            (0x37, false, 55),   // Keypad *
            (0x38, false, 56),   // Left Alt
            (0x39, false, 57),   // Space
            (0x3A, false, 58),   // Caps Lock
            (0x3B, false, 59),   // F1
            (0x3C, false, 60),   // F2
            (0x3D, false, 61),   // F3
            (0x3E, false, 62),   // F4
            (0x3F, false, 63),   // F5
            (0x40, false, 64),   // F6
            (0x41, false, 65),   // F7
            (0x42, false, 66),   // F8
            (0x43, false, 67),   // F9
            (0x44, false, 68),   // F10
            (0x45, false, 69),   // Num Lock
            (0x46, false, 70),   // Scroll Lock
            (0x47, false, 71),   // Keypad 7
            (0x48, false, 72),   // Keypad 8
            (0x49, false, 73),   // Keypad 9
            (0x4A, false, 74),   // Keypad -
            (0x4B, false, 75),   // Keypad 4
            (0x4C, false, 76),   // Keypad 5
            (0x4D, false, 77),   // Keypad 6
            (0x4E, false, 78),   // Keypad +
            (0x4F, false, 79),   // Keypad 1
            (0x50, false, 80),   // Keypad 2
            (0x51, false, 81),   // Keypad 3
            (0x52, false, 82),   // Keypad 0
            (0x53, false, 83),   // Keypad .
            (0x56, false, 86),   // The extra key an ISO keyboard has beside the left Shift
            (0x57, false, 87),   // F11
            (0x58, false, 88),   // F12

            // Behind the E0 prefix the same final bytes mean something else.
            (0x1C, true, 96),    // Keypad Enter, which is not the Enter above
            (0x1D, true, 97),    // Right Control
            (0x35, true, 98),    // Keypad /
            (0x37, true, 99),    // Print Screen
            (0x38, true, 100),   // Right Alt
            (0x47, true, 102),   // Home
            (0x48, true, 103),   // Up
            (0x49, true, 104),   // Page Up
            (0x4B, true, 105),   // Left
            (0x4D, true, 106),   // Right
            (0x4F, true, 107),   // End
            (0x50, true, 108),   // Down
            (0x51, true, 109),   // Page Down
            (0x52, true, 110),   // Insert
            (0x53, true, 111),   // Delete
            (0x5B, true, 125),   // Left Windows key
            (0x5C, true, 126),   // Right Windows key
            (0x5D, true, 127),   // The Menu key
        ];

        return rows.ToFrozenDictionary(row => (row.ScanCode, row.Extended), row => row.KeyCode);
    }
}
