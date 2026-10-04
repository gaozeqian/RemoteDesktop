using System.Globalization;

namespace RemoteDesktop.Features.Terminal.Emulation;

/// <summary>A cell colour: the terminal's default, one of the 256 palette entries, or 24-bit RGB. Packed into an integer to keep cells small.</summary>
public readonly record struct TerminalColor(uint Packed)
{
    private const int KindShift = 24;

    public static TerminalColor Default => default;

    public bool IsDefault => Packed == 0;

    public bool IsIndexed => Packed >> KindShift == 1;

    public bool IsRgb => Packed >> KindShift == 2;

    /// <summary>Palette index 0-255 (0-7 normal, 8-15 bright, 16-231 colour cube, 232-255 greys).</summary>
    public int Index => (int)(Packed & 0xFF);

    public byte R => (byte)(Packed >> 16);

    public byte G => (byte)(Packed >> 8);

    public byte B => (byte)Packed;

    public static TerminalColor FromIndex(int index) => new((1u << KindShift) | (uint)(index & 0xFF));

    public static TerminalColor FromRgb(byte r, byte g, byte b) => new((2u << KindShift) | ((uint)r << 16) | ((uint)g << 8) | b);
}

[Flags]
public enum CellAttributes : ushort
{
    None = 0,
    Bold = 1,
    Faint = 2,
    Italic = 4,
    Underline = 8,
    Blink = 16,
    Inverse = 32,
    Hidden = 64,
    Strikethrough = 128,

    /// <summary>The first half of a double-width character.</summary>
    Wide = 256,

    /// <summary>The second half of a double-width character; it draws nothing itself.</summary>
    WideTrail = 512,
}

/// <summary>One character position of the screen.</summary>
public struct TerminalCell
{
    /// <summary>The Unicode code point shown here; 0 for an empty cell.</summary>
    public int Character { get; set; }

    public TerminalColor Foreground { get; set; }

    public TerminalColor Background { get; set; }

    public CellAttributes Flags { get; set; }

    public readonly bool IsBlank => (Character == 0 || Character == ' ') && Background.IsDefault && (Flags & CellAttributes.Inverse) == 0;
}

/// <summary>A row of cells. <see cref="Wrapped"/> says the row continues the one above it (one long line that was wrapped), which copying uses to join them.</summary>
public sealed class TerminalLine(int columns)
{
    public TerminalCell[] Cells { get; private set; } = new TerminalCell[columns];

    public bool Wrapped { get; set; }

    public int Length => Cells.Length;

    public TerminalCell this[int column] => column >= 0 && column < Cells.Length ? Cells[column] : default;

    /// <summary>Changes the number of columns, keeping what fits.</summary>
    public void Resize(int columns)
    {
        if (columns != Cells.Length)
        {
            var resized = new TerminalCell[columns];
            Array.Copy(Cells, resized, Math.Min(columns, Cells.Length));
            Cells = resized;
        }
    }

    public TerminalLine Clone()
    {
        var copy = new TerminalLine(Cells.Length) { Wrapped = Wrapped };
        Array.Copy(Cells, copy.Cells, Cells.Length);
        return copy;
    }
}

/// <summary>The oldest lines that scrolled off the top, up to a limit.</summary>
internal sealed class ScrollbackBuffer(int capacity)
{
    private readonly TerminalLine[] _lines = new TerminalLine[Math.Max(1, capacity)];
    private int _start;

    public int Count { get; private set; }

    public TerminalLine this[int index] => _lines[(_start + index) % _lines.Length];

    public void Add(TerminalLine line)
    {
        if (Count < _lines.Length)
        {
            _lines[(_start + Count) % _lines.Length] = line;
            Count++;
        }
        else
        {
            _lines[_start] = line;
            _start = (_start + 1) % _lines.Length;
        }
    }

    public TerminalLine? RemoveLast()
    {
        if (Count == 0)
        {
            return null;
        }

        var index = (_start + Count - 1) % _lines.Length;
        var line = _lines[index];
        _lines[index] = null!;
        Count--;
        return line;
    }

    public void Clear()
    {
        Array.Clear(_lines);
        _start = 0;
        Count = 0;
    }
}

/// <summary>How many columns a character takes on a terminal screen.</summary>
public static class CharWidth
{
    private static readonly (int First, int Last)[] WideRanges =
    [
        (0x1100, 0x115F), (0x231A, 0x231B), (0x23E9, 0x23EC), (0x2614, 0x2615), (0x2648, 0x2653), (0x267F, 0x267F),
        (0x2693, 0x2693), (0x26A1, 0x26A1), (0x26AA, 0x26AB), (0x26BD, 0x26BE), (0x26C4, 0x26C5), (0x26CE, 0x26CE),
        (0x26D4, 0x26D4), (0x26EA, 0x26EA), (0x26F2, 0x26F3), (0x26F5, 0x26F5), (0x26FA, 0x26FA), (0x26FD, 0x26FD),
        (0x2705, 0x2705), (0x270A, 0x270B), (0x2728, 0x2728), (0x274C, 0x274C), (0x274E, 0x274E), (0x2753, 0x2755),
        (0x2757, 0x2757), (0x2795, 0x2797), (0x27B0, 0x27B0), (0x27BF, 0x27BF), (0x2B1B, 0x2B1C), (0x2B50, 0x2B50),
        (0x2B55, 0x2B55), (0x2E80, 0x303E), (0x3041, 0x33FF), (0x3400, 0x4DBF), (0x4E00, 0x9FFF), (0xA000, 0xA4CF),
        (0xA960, 0xA97F), (0xAC00, 0xD7A3), (0xF900, 0xFAFF), (0xFE10, 0xFE19), (0xFE30, 0xFE6F), (0xFF00, 0xFF60),
        (0xFFE0, 0xFFE6), (0x1F300, 0x1F64F), (0x1F680, 0x1F6FF), (0x1F900, 0x1F9FF), (0x1FA70, 0x1FAFF),
        (0x20000, 0x2FFFD), (0x30000, 0x3FFFD),
    ];

    /// <returns>0 for combining marks and invisible joiners, 2 for East Asian wide characters and emoji, otherwise 1.</returns>
    public static int Of(int codePoint)
    {
        if (codePoint < 0x300)
        {
            return 1;
        }

        if (codePoint is 0x200B or 0x200C or 0x200D or 0x2060 or 0xFEFF or (>= 0xFE00 and <= 0xFE0F))
        {
            return 0;
        }

        if (IsWide(codePoint))
        {
            return 2;
        }

        if (codePoint < 0x10FFFF && !(codePoint is >= 0xD800 and <= 0xDFFF))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(codePoint);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
            {
                return 0;
            }
        }

        return 1;
    }

    private static bool IsWide(int codePoint)
    {
        var low = 0;
        var high = WideRanges.Length - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var (first, last) = WideRanges[mid];
            if (codePoint < first)
            {
                high = mid - 1;
            }
            else if (codePoint > last)
            {
                low = mid + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }
}
