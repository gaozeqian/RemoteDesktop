using System.Collections.Frozen;
using RemoteDesktop.Platform.MacOS.Interop;

namespace RemoteDesktop.Platform.MacOS.Input;

/// <summary>
/// PC scan code set 1 (the wire format of key events) → macOS virtual key code (<c>CGKeyCode</c>).
/// </summary>
/// <remarks>
/// <para>
/// The controller sends hardware scan codes so that the host's own keyboard layout decides what character comes out,
/// exactly as a locally attached keyboard would. macOS has no scan code concept, so the mapping is by <em>physical
/// key position</em>: each entry names the key at the same place on the keyboard. A French host therefore still types
/// the character its own layout puts on that key.
/// </para>
/// <para>
/// Keys with no equivalent are simply absent and dropped. That is deliberate: <c>PrintScreen</c> and <c>ScrollLock</c>
/// have nowhere to go, and inventing a substitute would be worse than doing nothing.
/// </para>
/// </remarks>
internal static class ScanCodeToMacKey
{
    /// <summary>ANSI/ISO key positions on a US layout, from <c>HIToolbox/Events.h</c>.</summary>
    private static class MacKey
    {
        public const ushort A = 0x00;
        public const ushort S = 0x01;
        public const ushort D = 0x02;
        public const ushort F = 0x03;
        public const ushort H = 0x04;
        public const ushort G = 0x05;
        public const ushort Z = 0x06;
        public const ushort X = 0x07;
        public const ushort C = 0x08;
        public const ushort V = 0x09;
        public const ushort IsoSection = 0x0A;
        public const ushort B = 0x0B;
        public const ushort Q = 0x0C;
        public const ushort W = 0x0D;
        public const ushort E = 0x0E;
        public const ushort R = 0x0F;
        public const ushort Y = 0x10;
        public const ushort T = 0x11;
        public const ushort Digit1 = 0x12;
        public const ushort Digit2 = 0x13;
        public const ushort Digit3 = 0x14;
        public const ushort Digit4 = 0x15;
        public const ushort Digit6 = 0x16;
        public const ushort Digit5 = 0x17;
        public const ushort Equal = 0x18;
        public const ushort Digit9 = 0x19;
        public const ushort Digit7 = 0x1A;
        public const ushort Minus = 0x1B;
        public const ushort Digit8 = 0x1C;
        public const ushort Digit0 = 0x1D;
        public const ushort BracketRight = 0x1E;
        public const ushort O = 0x1F;
        public const ushort U = 0x20;
        public const ushort BracketLeft = 0x21;
        public const ushort I = 0x22;
        public const ushort P = 0x23;
        public const ushort Enter = 0x24;
        public const ushort L = 0x25;
        public const ushort J = 0x26;
        public const ushort Quote = 0x27;
        public const ushort K = 0x28;
        public const ushort Semicolon = 0x29;
        public const ushort Backslash = 0x2A;
        public const ushort Comma = 0x2B;
        public const ushort Slash = 0x2C;
        public const ushort N = 0x2D;
        public const ushort M = 0x2E;
        public const ushort Period = 0x2F;
        public const ushort Tab = 0x30;
        public const ushort Space = 0x31;
        public const ushort Backquote = 0x32;
        public const ushort Backspace = 0x33;
        public const ushort Escape = 0x35;
        public const ushort CommandRight = 0x36;
        public const ushort CommandLeft = 0x37;
        public const ushort ShiftLeft = 0x38;
        public const ushort CapsLock = 0x39;
        public const ushort OptionLeft = 0x3A;
        public const ushort ControlLeft = 0x3B;
        public const ushort ShiftRight = 0x3C;
        public const ushort OptionRight = 0x3D;
        public const ushort ControlRight = 0x3E;
        public const ushort Function = 0x3F;
        public const ushort KeypadDecimal = 0x41;
        public const ushort KeypadMultiply = 0x43;
        public const ushort KeypadPlus = 0x45;
        public const ushort KeypadClear = 0x47;
        public const ushort KeypadDivide = 0x4B;
        public const ushort KeypadEnter = 0x4C;
        public const ushort KeypadMinus = 0x4E;
        public const ushort F17 = 0x40;
        public const ushort Keypad0 = 0x52;
        public const ushort Keypad1 = 0x53;
        public const ushort Keypad2 = 0x54;
        public const ushort Keypad3 = 0x55;
        public const ushort Keypad4 = 0x56;
        public const ushort Keypad5 = 0x57;
        public const ushort Keypad6 = 0x58;
        public const ushort Keypad7 = 0x59;
        public const ushort Keypad8 = 0x5B;
        public const ushort Keypad9 = 0x5C;
        public const ushort F5 = 0x60;
        public const ushort F6 = 0x61;
        public const ushort F7 = 0x62;
        public const ushort F3 = 0x63;
        public const ushort F8 = 0x64;
        public const ushort F9 = 0x65;
        public const ushort F11 = 0x67;
        public const ushort F13 = 0x69;
        public const ushort F16 = 0x6A;
        public const ushort F14 = 0x6B;
        public const ushort F10 = 0x6D;
        public const ushort ContextMenu = 0x6E;
        public const ushort F12 = 0x6F;
        public const ushort F15 = 0x71;
        public const ushort Help = 0x72;
        public const ushort Home = 0x73;
        public const ushort PageUp = 0x74;
        public const ushort ForwardDelete = 0x75;
        public const ushort F4 = 0x76;
        public const ushort End = 0x77;
        public const ushort F2 = 0x78;
        public const ushort PageDown = 0x79;
        public const ushort F1 = 0x7A;
        public const ushort ArrowLeft = 0x7B;
        public const ushort ArrowRight = 0x7C;
        public const ushort ArrowDown = 0x7D;
        public const ushort ArrowUp = 0x7E;
    }

    private const int ExtendedBit = 0x100;

    private static readonly FrozenDictionary<int, ushort> Map = new Dictionary<int, ushort>
    {
        [0x01] = MacKey.Escape,
        [0x02] = MacKey.Digit1,
        [0x03] = MacKey.Digit2,
        [0x04] = MacKey.Digit3,
        [0x05] = MacKey.Digit4,
        [0x06] = MacKey.Digit5,
        [0x07] = MacKey.Digit6,
        [0x08] = MacKey.Digit7,
        [0x09] = MacKey.Digit8,
        [0x0A] = MacKey.Digit9,
        [0x0B] = MacKey.Digit0,
        [0x0C] = MacKey.Minus,
        [0x0D] = MacKey.Equal,
        [0x0E] = MacKey.Backspace,
        [0x0F] = MacKey.Tab,
        [0x10] = MacKey.Q,
        [0x11] = MacKey.W,
        [0x12] = MacKey.E,
        [0x13] = MacKey.R,
        [0x14] = MacKey.T,
        [0x15] = MacKey.Y,
        [0x16] = MacKey.U,
        [0x17] = MacKey.I,
        [0x18] = MacKey.O,
        [0x19] = MacKey.P,
        [0x1A] = MacKey.BracketLeft,
        [0x1B] = MacKey.BracketRight,
        [0x1C] = MacKey.Enter,
        [0x1D] = MacKey.ControlLeft,
        [0x1E] = MacKey.A,
        [0x1F] = MacKey.S,
        [0x20] = MacKey.D,
        [0x21] = MacKey.F,
        [0x22] = MacKey.G,
        [0x23] = MacKey.H,
        [0x24] = MacKey.J,
        [0x25] = MacKey.K,
        [0x26] = MacKey.L,
        [0x27] = MacKey.Semicolon,
        [0x28] = MacKey.Quote,
        [0x29] = MacKey.Backquote,
        [0x2A] = MacKey.ShiftLeft,
        [0x2B] = MacKey.Backslash,
        [0x2C] = MacKey.Z,
        [0x2D] = MacKey.X,
        [0x2E] = MacKey.C,
        [0x2F] = MacKey.V,
        [0x30] = MacKey.B,
        [0x31] = MacKey.N,
        [0x32] = MacKey.M,
        [0x33] = MacKey.Comma,
        [0x34] = MacKey.Period,
        [0x35] = MacKey.Slash,
        [0x36] = MacKey.ShiftRight,
        [0x37] = MacKey.KeypadMultiply,
        [0x38] = MacKey.OptionLeft,
        [0x39] = MacKey.Space,
        [0x3A] = MacKey.CapsLock,
        [0x3B] = MacKey.F1,
        [0x3C] = MacKey.F2,
        [0x3D] = MacKey.F3,
        [0x3E] = MacKey.F4,
        [0x3F] = MacKey.F5,
        [0x40] = MacKey.F6,
        [0x41] = MacKey.F7,
        [0x42] = MacKey.F8,
        [0x43] = MacKey.F9,
        [0x44] = MacKey.F10,
        [0x45] = MacKey.KeypadClear,
        [0x47] = MacKey.Keypad7,
        [0x48] = MacKey.Keypad8,
        [0x49] = MacKey.Keypad9,
        [0x4A] = MacKey.KeypadMinus,
        [0x4B] = MacKey.Keypad4,
        [0x4C] = MacKey.Keypad5,
        [0x4D] = MacKey.Keypad6,
        [0x4E] = MacKey.KeypadPlus,
        [0x4F] = MacKey.Keypad1,
        [0x50] = MacKey.Keypad2,
        [0x51] = MacKey.Keypad3,
        [0x52] = MacKey.Keypad0,
        [0x53] = MacKey.KeypadDecimal,
        [0x56] = MacKey.IsoSection,
        [0x57] = MacKey.F11,
        [0x58] = MacKey.F12,
        [0x64] = MacKey.F13,
        [0x65] = MacKey.F14,
        [0x66] = MacKey.F15,

        // E0-prefixed keys.
        [0x1C | ExtendedBit] = MacKey.KeypadEnter,
        [0x1D | ExtendedBit] = MacKey.ControlRight,
        [0x35 | ExtendedBit] = MacKey.KeypadDivide,
        [0x38 | ExtendedBit] = MacKey.OptionRight,
        [0x47 | ExtendedBit] = MacKey.Home,
        [0x48 | ExtendedBit] = MacKey.ArrowUp,
        [0x49 | ExtendedBit] = MacKey.PageUp,
        [0x4B | ExtendedBit] = MacKey.ArrowLeft,
        [0x4D | ExtendedBit] = MacKey.ArrowRight,
        [0x4F | ExtendedBit] = MacKey.End,
        [0x50 | ExtendedBit] = MacKey.ArrowDown,
        [0x51 | ExtendedBit] = MacKey.PageDown,
        [0x52 | ExtendedBit] = MacKey.Help,
        [0x53 | ExtendedBit] = MacKey.ForwardDelete,
        [0x5B | ExtendedBit] = MacKey.CommandLeft,
        [0x5C | ExtendedBit] = MacKey.CommandRight,
        [0x5D | ExtendedBit] = MacKey.ContextMenu,
    }.ToFrozenDictionary();

    /// <summary>Maps a scan code, or returns false when the key has no macOS equivalent.</summary>
    public static bool TryGet(int scanCode, bool extended, out ushort macKey) =>
        Map.TryGetValue(scanCode | (extended ? ExtendedBit : 0), out macKey);

    /// <summary>
    /// The modifier flag a key carries, or zero when it is not a modifier.
    /// </summary>
    /// <remarks>
    /// Modifiers cannot be injected as ordinary key downs on macOS: they have to go out as <c>FlagsChanged</c> events
    /// carrying the accumulated flag set, and every following key event has to repeat that set. This is what tells
    /// the injector which keys those are.
    /// </remarks>
    public static ulong ModifierFlag(ushort macKey) => macKey switch
    {
        MacKey.ShiftLeft or MacKey.ShiftRight => CoreGraphics.FlagMaskShift,
        MacKey.ControlLeft or MacKey.ControlRight => CoreGraphics.FlagMaskControl,
        MacKey.OptionLeft or MacKey.OptionRight => CoreGraphics.FlagMaskAlternate,
        MacKey.CommandLeft or MacKey.CommandRight => CoreGraphics.FlagMaskCommand,
        MacKey.CapsLock => CoreGraphics.FlagMaskAlphaShift,
        MacKey.Function => CoreGraphics.FlagMaskSecondaryFn,
        _ => 0,
    };
}
