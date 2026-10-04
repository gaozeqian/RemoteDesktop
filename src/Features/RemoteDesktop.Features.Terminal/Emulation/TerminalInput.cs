using System.Globalization;
using System.Text;

namespace RemoteDesktop.Features.Terminal.Emulation;

/// <summary>Keys that are sent as escape sequences; letters, digits and symbols go through <see cref="TerminalInput.EncodeText"/> or <see cref="TerminalInput.EncodeControl"/>.</summary>
public enum TerminalKey
{
    Enter,
    Tab,
    Backspace,
    Escape,
    Up,
    Down,
    Left,
    Right,
    Home,
    End,
    PageUp,
    PageDown,
    Insert,
    Delete,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
}

[Flags]
public enum TerminalModifiers
{
    None = 0,
    Shift = 1,
    Alt = 2,
    Control = 4,
}

public enum MouseAction
{
    Press,
    Release,
    Move,
    WheelUp,
    WheelDown,
}

/// <summary>What a terminal program expects to receive for keys, pastes and mouse events.</summary>
public static class TerminalInput
{
    private const string Esc = "\u001b";

    /// <summary>The bytes for a special key, or <c>null</c> when the combination has none.</summary>
    /// <param name="applicationCursorKeys">The program switched on application cursor keys (DECCKM), which changes arrows, Home and End.</param>
    public static byte[] EncodeKey(TerminalKey key, TerminalModifiers modifiers, bool applicationCursorKeys)
    {
        var code = 1 + (modifiers.HasFlag(TerminalModifiers.Shift) ? 1 : 0) + (modifiers.HasFlag(TerminalModifiers.Alt) ? 2 : 0) + (modifiers.HasFlag(TerminalModifiers.Control) ? 4 : 0);
        var modified = code > 1;
        string text;
        switch (key)
        {
            case TerminalKey.Enter:
                text = modifiers.HasFlag(TerminalModifiers.Alt) ? Esc + "\r" : "\r";
                break;
            case TerminalKey.Tab:
                text = modifiers.HasFlag(TerminalModifiers.Shift) ? Esc + "[Z" : modifiers.HasFlag(TerminalModifiers.Alt) ? Esc + "\t" : "\t";
                break;
            case TerminalKey.Backspace:
                // DEL is what shells expect; Ctrl+Backspace is the usual "delete word" (BS).
                text = modifiers.HasFlag(TerminalModifiers.Control) ? "\b" : modifiers.HasFlag(TerminalModifiers.Alt) ? Esc + "\u007f" : "\u007f";
                break;
            case TerminalKey.Escape:
                text = Esc;
                break;
            case TerminalKey.Up:
            case TerminalKey.Down:
            case TerminalKey.Right:
            case TerminalKey.Left:
            case TerminalKey.Home:
            case TerminalKey.End:
                var letter = key switch
                {
                    TerminalKey.Up => 'A',
                    TerminalKey.Down => 'B',
                    TerminalKey.Right => 'C',
                    TerminalKey.Left => 'D',
                    TerminalKey.Home => 'H',
                    _ => 'F',
                };
                text = modified
                    ? string.Create(CultureInfo.InvariantCulture, $"{Esc}[1;{code}{letter}")
                    : applicationCursorKeys ? $"{Esc}O{letter}" : $"{Esc}[{letter}";
                break;
            case TerminalKey.F1:
            case TerminalKey.F2:
            case TerminalKey.F3:
            case TerminalKey.F4:
                var function = (char)('P' + (key - TerminalKey.F1));
                text = modified ? string.Create(CultureInfo.InvariantCulture, $"{Esc}[1;{code}{function}") : $"{Esc}O{function}";
                break;
            default:
                var number = key switch
                {
                    TerminalKey.Insert => 2,
                    TerminalKey.Delete => 3,
                    TerminalKey.PageUp => 5,
                    TerminalKey.PageDown => 6,
                    TerminalKey.F5 => 15,
                    TerminalKey.F6 => 17,
                    TerminalKey.F7 => 18,
                    TerminalKey.F8 => 19,
                    TerminalKey.F9 => 20,
                    TerminalKey.F10 => 21,
                    TerminalKey.F11 => 23,
                    _ => 24,
                };
                text = modified
                    ? string.Create(CultureInfo.InvariantCulture, $"{Esc}[{number};{code}~")
                    : string.Create(CultureInfo.InvariantCulture, $"{Esc}[{number}~");
                break;
        }

        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>
    /// A character typed with Ctrl and/or Alt held: Ctrl+letter becomes the control code, Alt adds an escape in front.
    /// Returns <c>null</c> for combinations that have no terminal meaning (Ctrl+digit and so on), so the caller can ignore them.
    /// </summary>
    public static byte[]? EncodeControl(char character, TerminalModifiers modifiers)
    {
        var alt = modifiers.HasFlag(TerminalModifiers.Alt);
        char output;
        if (modifiers.HasFlag(TerminalModifiers.Control))
        {
            output = char.ToUpperInvariant(character) switch
            {
                >= 'A' and <= 'Z' => (char)(char.ToUpperInvariant(character) - 'A' + 1),
                '@' or ' ' or '2' => '\0',
                '[' or '3' => '\u001b',
                '\\' or '4' => '\u001c',
                ']' or '5' => '\u001d',
                '^' or '6' => '\u001e',
                '_' or '-' or '7' => '\u001f',
                '?' or '8' => '\u007f',
                _ => char.MaxValue,
            };
            if (output == char.MaxValue)
            {
                return null;
            }
        }
        else
        {
            output = character;
        }

        var text = alt ? Esc + output : output.ToString();
        return Encoding.UTF8.GetBytes(text);
    }

    public static byte[] EncodeText(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>Pasted text as the shell should see it: line breaks become Enter, and the program is told it is a paste when it asked to be.</summary>
    public static byte[] EncodePaste(string text, bool bracketed)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
        if (bracketed)
        {
            // A paste must not be able to end the bracket early and smuggle in commands.
            normalized = normalized.Replace(Esc + "[201~", string.Empty, StringComparison.Ordinal);
            normalized = $"{Esc}[200~{normalized}{Esc}[201~";
        }

        return Encoding.UTF8.GetBytes(normalized);
    }

    /// <summary>A mouse report for a program that asked for them, or <c>null</c> when it cannot be expressed.</summary>
    /// <param name="button">0 left, 1 middle, 2 right.</param>
    /// <param name="column">Zero-based cell column.</param>
    /// <param name="row">Zero-based cell row.</param>
    public static byte[]? EncodeMouse(MouseAction action, int button, int column, int row, TerminalModifiers modifiers, bool sgr)
    {
        var code = action switch
        {
            MouseAction.WheelUp => 64,
            MouseAction.WheelDown => 65,
            _ => button < 0 ? 3 : Math.Clamp(button, 0, 2),
        };
        if (action == MouseAction.Move)
        {
            code += 32;
        }

        code += (modifiers.HasFlag(TerminalModifiers.Shift) ? 4 : 0) + (modifiers.HasFlag(TerminalModifiers.Alt) ? 8 : 0) + (modifiers.HasFlag(TerminalModifiers.Control) ? 16 : 0);
        var x = column + 1;
        var y = row + 1;
        if (sgr)
        {
            var final = action == MouseAction.Release ? 'm' : 'M';
            return Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{Esc}[<{code};{x};{y}{final}"));
        }

        // The old encoding has no release button and cannot address beyond column/row 222.
        if (x > 222 || y > 222)
        {
            return null;
        }

        var legacy = action == MouseAction.Release ? 3 + (code & ~3) : code;
        return [0x1B, (byte)'[', (byte)'M', (byte)(32 + legacy), (byte)(32 + x), (byte)(32 + y)];
    }
}
