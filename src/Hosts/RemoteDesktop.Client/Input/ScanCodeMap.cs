using System.Collections.Frozen;
using Avalonia.Input;

namespace RemoteDesktop.Client.Input;

/// <summary>
/// Physical key → PC scan code set 1 (the wire format of key events). Physical keys are layout-independent, so the
/// host interprets them with its own keyboard layout, exactly as a locally attached keyboard would.
/// </summary>
internal static class ScanCodeMap
{
    private static readonly FrozenDictionary<PhysicalKey, (int ScanCode, bool Extended)> Map = new Dictionary<PhysicalKey, (int, bool)>
    {
        [PhysicalKey.Escape] = (0x01, false),
        [PhysicalKey.Digit1] = (0x02, false),
        [PhysicalKey.Digit2] = (0x03, false),
        [PhysicalKey.Digit3] = (0x04, false),
        [PhysicalKey.Digit4] = (0x05, false),
        [PhysicalKey.Digit5] = (0x06, false),
        [PhysicalKey.Digit6] = (0x07, false),
        [PhysicalKey.Digit7] = (0x08, false),
        [PhysicalKey.Digit8] = (0x09, false),
        [PhysicalKey.Digit9] = (0x0A, false),
        [PhysicalKey.Digit0] = (0x0B, false),
        [PhysicalKey.Minus] = (0x0C, false),
        [PhysicalKey.Equal] = (0x0D, false),
        [PhysicalKey.Backspace] = (0x0E, false),
        [PhysicalKey.Tab] = (0x0F, false),
        [PhysicalKey.Q] = (0x10, false),
        [PhysicalKey.W] = (0x11, false),
        [PhysicalKey.E] = (0x12, false),
        [PhysicalKey.R] = (0x13, false),
        [PhysicalKey.T] = (0x14, false),
        [PhysicalKey.Y] = (0x15, false),
        [PhysicalKey.U] = (0x16, false),
        [PhysicalKey.I] = (0x17, false),
        [PhysicalKey.O] = (0x18, false),
        [PhysicalKey.P] = (0x19, false),
        [PhysicalKey.BracketLeft] = (0x1A, false),
        [PhysicalKey.BracketRight] = (0x1B, false),
        [PhysicalKey.Enter] = (0x1C, false),
        [PhysicalKey.ControlLeft] = (0x1D, false),
        [PhysicalKey.A] = (0x1E, false),
        [PhysicalKey.S] = (0x1F, false),
        [PhysicalKey.D] = (0x20, false),
        [PhysicalKey.F] = (0x21, false),
        [PhysicalKey.G] = (0x22, false),
        [PhysicalKey.H] = (0x23, false),
        [PhysicalKey.J] = (0x24, false),
        [PhysicalKey.K] = (0x25, false),
        [PhysicalKey.L] = (0x26, false),
        [PhysicalKey.Semicolon] = (0x27, false),
        [PhysicalKey.Quote] = (0x28, false),
        [PhysicalKey.Backquote] = (0x29, false),
        [PhysicalKey.ShiftLeft] = (0x2A, false),
        [PhysicalKey.Backslash] = (0x2B, false),
        [PhysicalKey.Z] = (0x2C, false),
        [PhysicalKey.X] = (0x2D, false),
        [PhysicalKey.C] = (0x2E, false),
        [PhysicalKey.V] = (0x2F, false),
        [PhysicalKey.B] = (0x30, false),
        [PhysicalKey.N] = (0x31, false),
        [PhysicalKey.M] = (0x32, false),
        [PhysicalKey.Comma] = (0x33, false),
        [PhysicalKey.Period] = (0x34, false),
        [PhysicalKey.Slash] = (0x35, false),
        [PhysicalKey.ShiftRight] = (0x36, false),
        [PhysicalKey.NumPadMultiply] = (0x37, false),
        [PhysicalKey.AltLeft] = (0x38, false),
        [PhysicalKey.Space] = (0x39, false),
        [PhysicalKey.CapsLock] = (0x3A, false),
        [PhysicalKey.F1] = (0x3B, false),
        [PhysicalKey.F2] = (0x3C, false),
        [PhysicalKey.F3] = (0x3D, false),
        [PhysicalKey.F4] = (0x3E, false),
        [PhysicalKey.F5] = (0x3F, false),
        [PhysicalKey.F6] = (0x40, false),
        [PhysicalKey.F7] = (0x41, false),
        [PhysicalKey.F8] = (0x42, false),
        [PhysicalKey.F9] = (0x43, false),
        [PhysicalKey.F10] = (0x44, false),
        [PhysicalKey.NumLock] = (0x45, false),
        [PhysicalKey.ScrollLock] = (0x46, false),
        [PhysicalKey.NumPad7] = (0x47, false),
        [PhysicalKey.NumPad8] = (0x48, false),
        [PhysicalKey.NumPad9] = (0x49, false),
        [PhysicalKey.NumPadSubtract] = (0x4A, false),
        [PhysicalKey.NumPad4] = (0x4B, false),
        [PhysicalKey.NumPad5] = (0x4C, false),
        [PhysicalKey.NumPad6] = (0x4D, false),
        [PhysicalKey.NumPadAdd] = (0x4E, false),
        [PhysicalKey.NumPad1] = (0x4F, false),
        [PhysicalKey.NumPad2] = (0x50, false),
        [PhysicalKey.NumPad3] = (0x51, false),
        [PhysicalKey.NumPad0] = (0x52, false),
        [PhysicalKey.NumPadDecimal] = (0x53, false),
        [PhysicalKey.IntlBackslash] = (0x56, false),
        [PhysicalKey.F11] = (0x57, false),
        [PhysicalKey.F12] = (0x58, false),
        [PhysicalKey.KanaMode] = (0x70, false),
        [PhysicalKey.IntlRo] = (0x73, false),
        [PhysicalKey.Convert] = (0x79, false),
        [PhysicalKey.NonConvert] = (0x7B, false),
        [PhysicalKey.IntlYen] = (0x7D, false),

        // E0-prefixed keys.
        [PhysicalKey.NumPadEnter] = (0x1C, true),
        [PhysicalKey.ControlRight] = (0x1D, true),
        [PhysicalKey.NumPadDivide] = (0x35, true),
        [PhysicalKey.PrintScreen] = (0x37, true),
        [PhysicalKey.AltRight] = (0x38, true),
        [PhysicalKey.Home] = (0x47, true),
        [PhysicalKey.ArrowUp] = (0x48, true),
        [PhysicalKey.PageUp] = (0x49, true),
        [PhysicalKey.ArrowLeft] = (0x4B, true),
        [PhysicalKey.ArrowRight] = (0x4D, true),
        [PhysicalKey.End] = (0x4F, true),
        [PhysicalKey.ArrowDown] = (0x50, true),
        [PhysicalKey.PageDown] = (0x51, true),
        [PhysicalKey.Insert] = (0x52, true),
        [PhysicalKey.Delete] = (0x53, true),
        [PhysicalKey.MetaLeft] = (0x5B, true),
        [PhysicalKey.MetaRight] = (0x5C, true),
        [PhysicalKey.ContextMenu] = (0x5D, true),
    }.ToFrozenDictionary();

    public static bool TryGet(PhysicalKey key, out int scanCode, out bool extended)
    {
        if (Map.TryGetValue(key, out var entry))
        {
            (scanCode, extended) = entry;
            return true;
        }

        scanCode = 0;
        extended = false;
        return false;
    }
}
