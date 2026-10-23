using System.Text;

namespace RemoteDesktop.Features.Terminal.Emulation;

// The escape sequence parser: a state machine after the one DEC documented for its terminals (and xterm follows),
// with UTF-8 decoding in front of it.
public sealed partial class TerminalEmulator
{
    private const int MaxParameters = 32;
    private const int MaxStringLength = 4096;

    // DEC special graphics, for codes 0x60-0x7E.
    private const string LineDrawing = "◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·";

    private readonly int[] _parameters = new int[MaxParameters];
    private readonly bool[] _subParameter = new bool[MaxParameters];
    private readonly StringBuilder _intermediates = new();
    private readonly List<byte> _string = [];
    private State _state;
    private int _parameterCount;
    private int _currentParameter = -1;
    private bool _currentIsSub;
    private char _marker;
    private int _utf8Remaining;
    private int _utf8CodePoint;
    private bool _bellRaised;
    private bool _lineDrawingG0;
    private bool _lineDrawingG1;
    private bool _shifted;
    private bool _stringIsOsc;

    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParameter,
        CsiIntermediate,
        CsiIgnore,
        String,
    }

    private void Process(byte b)
    {
        // These end whatever is going on, in every state.
        if (b == 0x18 || b == 0x1A)
        {
            _utf8Remaining = 0;
            _state = State.Ground;
            return;
        }

        if (b == 0x1B)
        {
            FlushUtf8Error();
            if (_state == State.String)
            {
                FinishString();
            }

            EnterEscape();
            return;
        }

        if (_state == State.String)
        {
            if (b == 0x07 && _stringIsOsc)
            {
                FinishString();
                _state = State.Ground;
            }
            else if (_string.Count < MaxStringLength)
            {
                _string.Add(b);
            }

            return;
        }

        if (b < 0x20)
        {
            FlushUtf8Error();
            Execute(b);
            return;
        }

        switch (_state)
        {
            case State.Ground:
                Ground(b);
                break;
            case State.Escape:
                EscapeState(b);
                break;
            case State.EscapeIntermediate:
                if (b <= 0x2F)
                {
                    if (!TryAppendIntermediate(b)) { return; }
                }
                else if (b <= 0x7E)
                {
                    DispatchEscape((char)b);
                    _state = State.Ground;
                }

                break;
            case State.CsiEntry:
            case State.CsiParameter:
                CsiParameterState(b);
                break;
            case State.CsiIntermediate:
                if (b <= 0x2F)
                {
                    if (!TryAppendIntermediate(b)) { return; }
                }
                else if (b >= 0x40 && b <= 0x7E)
                {
                    DispatchCsi((char)b);
                    _state = State.Ground;
                }
                else
                {
                    _state = State.CsiIgnore;
                }

                break;
            case State.CsiIgnore:
                if (b >= 0x40 && b <= 0x7E)
                {
                    _state = State.Ground;
                }

                break;
            default:
                break;
        }
    }

    private void Ground(byte b)
    {
        if (b < 0x80)
        {
            FlushUtf8Error();
            if (b == 0x7F)
            {
                return;
            }

            PrintCharacter(b);
            return;
        }

        if (_utf8Remaining > 0)
        {
            if ((b & 0xC0) == 0x80)
            {
                _utf8CodePoint = (_utf8CodePoint << 6) | (b & 0x3F);
                if (--_utf8Remaining == 0)
                {
                    PrintCharacter(_utf8CodePoint is >= 0xD800 and <= 0xDFFF or > 0x10FFFF ? 0xFFFD : _utf8CodePoint);
                }

                return;
            }

            // A new sequence began before the old one was complete.
            _utf8Remaining = 0;
            PrintCharacter(0xFFFD);
        }

        if (b >= 0xC2 && b <= 0xDF)
        {
            _utf8Remaining = 1;
            _utf8CodePoint = b & 0x1F;
        }
        else if (b >= 0xE0 && b <= 0xEF)
        {
            _utf8Remaining = 2;
            _utf8CodePoint = b & 0x0F;
        }
        else if (b >= 0xF0 && b <= 0xF4)
        {
            _utf8Remaining = 3;
            _utf8CodePoint = b & 0x07;
        }
        else
        {
            PrintCharacter(0xFFFD);
        }
    }

    private void FlushUtf8Error()
    {
        if (_utf8Remaining > 0)
        {
            _utf8Remaining = 0;
            PrintCharacter(0xFFFD);
        }
    }

    private void PrintCharacter(int codePoint)
    {
        var lineDrawing = _shifted ? _lineDrawingG1 : _lineDrawingG0;
        if (lineDrawing && codePoint is >= 0x60 and <= 0x7E)
        {
            codePoint = LineDrawing[codePoint - 0x60];
        }

        Print(codePoint);
    }

    private void Execute(byte b)
    {
        switch (b)
        {
            case 0x07:
                _bellRaised = true;
                break;
            case 0x08:
                Backspace();
                break;
            case 0x09:
                Tab(1);
                break;
            case 0x0A:
            case 0x0B:
            case 0x0C:
                LineFeed();
                break;
            case 0x0D:
                CarriageReturn();
                break;
            case 0x0E:
                _shifted = true;
                break;
            case 0x0F:
                _shifted = false;
                break;
            default:
                break;
        }
    }

    private void EnterEscape()
    {
        _state = State.Escape;
        _intermediates.Clear();
    }

    private void EscapeState(byte b)
    {
        switch (b)
        {
            case (byte)'[':
                _state = State.CsiEntry;
                _parameterCount = 0;
                _currentParameter = -1;
                _currentIsSub = false;
                _marker = '\0';
                _intermediates.Clear();
                return;
            case (byte)']':
                _state = State.String;
                _stringIsOsc = true;
                _string.Clear();
                return;
            case (byte)'P':
            case (byte)'X':
            case (byte)'^':
            case (byte)'_':
                _state = State.String;
                _stringIsOsc = false;
                _string.Clear();
                return;
            default:
                break;
        }

        if (b <= 0x2F)
        {
            if (!TryAppendIntermediate(b)) { return; }
            _state = State.EscapeIntermediate;
        }
        else if (b <= 0x7E)
        {
            DispatchEscape((char)b);
            _state = State.Ground;
        }
    }

    private void CsiParameterState(byte b)
    {
        if (b >= '0' && b <= '9')
        {
            _currentParameter = Math.Max(_currentParameter, 0) * 10 + (b - '0');
            if (_currentParameter > 65535)
            {
                _currentParameter = 65535;
            }

            _state = State.CsiParameter;
        }
        else if (b == ';' || b == ':')
        {
            PushParameter();
            _currentIsSub = b == ':';
            _state = State.CsiParameter;
        }
        else if (b >= '<' && b <= '?')
        {
            if (_state == State.CsiEntry)
            {
                _marker = (char)b;
                _state = State.CsiParameter;
            }
            else
            {
                _state = State.CsiIgnore;
            }
        }
        else if (b <= 0x2F)
        {
            if (!TryAppendIntermediate(b)) { return; }
            _state = State.CsiIntermediate;
        }
        else if (b >= 0x40 && b <= 0x7E)
        {
            if (_currentParameter >= 0 || _parameterCount > 0)
            {
                PushParameter();
            }

            DispatchCsi((char)b);
            _state = State.Ground;
        }
        else
        {
            _state = State.CsiIgnore;
        }
    }

    private void PushParameter()
    {
        if (_parameterCount < MaxParameters)
        {
            _parameters[_parameterCount] = _currentParameter;
            _subParameter[_parameterCount] = _currentIsSub;
            _parameterCount++;
        }

        _currentParameter = -1;
        _currentIsSub = false;
    }

    /// <summary>The parameter at <paramref name="index"/>, or <paramref name="fallback"/> when it was left out (or zero where zero means "default").</summary>
    private int Param(int index, int fallback, bool zeroIsDefault = true)
    {
        if (index >= _parameterCount || _parameters[index] < 0 || (zeroIsDefault && _parameters[index] == 0))
        {
            return fallback;
        }

        return _parameters[index];
    }

    private void FinishString()
    {
        if (_stringIsOsc)
        {
            DispatchOsc(Encoding.UTF8.GetString([.. _string]));
        }

        _string.Clear();
        _state = State.Ground;
    }

    private void DispatchOsc(string text)
    {
        var separator = text.IndexOf(';', StringComparison.Ordinal);
        var command = separator < 0 ? text : text[..separator];
        var argument = separator < 0 ? string.Empty : text[(separator + 1)..];
        if (command is "0" or "2")
        {
            Title = argument;
            _dirty = true;
        }

        // Everything else (colour queries, hyperlinks, clipboard access, working directory) is deliberately ignored:
        // a program on the host has no business reading or writing the controller's clipboard.
    }

    private void DispatchEscape(char final)
    {
        var intermediates = _intermediates.ToString();
        if (intermediates.Length == 1)
        {
            switch (intermediates[0])
            {
                case '(':
                    _lineDrawingG0 = final == '0';
                    return;
                case ')':
                    _lineDrawingG1 = final == '0';
                    return;
                case '#' when final == '8':
                    for (var y = 0; y < Rows; y++)
                    {
                        for (var x = 0; x < Columns; x++)
                        {
                            _screen[y].Cells[x] = new TerminalCell { Character = 'E' };
                        }
                    }

                    _dirty = true;
                    return;
                default:
                    return;
            }
        }

        switch (final)
        {
            case '7':
                SaveCursor();
                break;
            case '8':
                RestoreCursor();
                break;
            case 'D':
                LineFeed();
                break;
            case 'E':
                CarriageReturn();
                LineFeed();
                break;
            case 'M':
                ReverseIndex();
                break;
            case 'H':
                _tabStops[Math.Min(_cursorX, Columns - 1)] = true;
                break;
            case 'c':
                FullReset();
                break;
            case '=':
                ApplicationKeypad = true;
                break;
            case '>':
                ApplicationKeypad = false;
                break;
            default:
                break;
        }
    }

    private void DispatchCsi(char final)
    {
        var intermediates = _intermediates.ToString();
        if (intermediates.Length > 0)
        {
            DispatchCsiWithIntermediate(intermediates, final);
            return;
        }

        if (_marker == '?')
        {
            if (final is 'h' or 'l')
            {
                for (var i = 0; i < Math.Max(_parameterCount, 1); i++)
                {
                    SetPrivateMode(Param(i, 0), final == 'h');
                }
            }

            return;
        }

        if (_marker == '>')
        {
            if (final == 'c')
            {
                Respond("\u001b[>0;10;1c");
            }

            return;
        }

        if (_marker != '\0')
        {
            return;
        }

        switch (final)
        {
            case '@':
                InsertCharacters(Param(0, 1));
                break;
            case 'A':
                _cursorY = Math.Max(_originMode ? _scrollTop : 0, _cursorY - Param(0, 1));
                _wrapPending = false;
                break;
            case 'B':
            case 'e':
                _cursorY = Math.Min(_originMode ? _scrollBottom : Rows - 1, _cursorY + Param(0, 1));
                _wrapPending = false;
                break;
            case 'C':
            case 'a':
                _cursorX = Math.Min(Columns - 1, _cursorX + Param(0, 1));
                _wrapPending = false;
                break;
            case 'D':
                _cursorX = Math.Max(0, _cursorX - Param(0, 1));
                _wrapPending = false;
                break;
            case 'E':
                _cursorY = Math.Min(_originMode ? _scrollBottom : Rows - 1, _cursorY + Param(0, 1));
                CarriageReturn();
                break;
            case 'F':
                _cursorY = Math.Max(_originMode ? _scrollTop : 0, _cursorY - Param(0, 1));
                CarriageReturn();
                break;
            case 'G':
            case '`':
                _cursorX = Math.Clamp(Param(0, 1) - 1, 0, Columns - 1);
                _wrapPending = false;
                break;
            case 'H':
            case 'f':
                MoveCursorTo(Param(0, 1) - 1, Param(1, 1) - 1);
                break;
            case 'I':
                Tab(Param(0, 1));
                break;
            case 'J':
                EraseInDisplay(Param(0, 0, zeroIsDefault: false));
                break;
            case 'K':
                EraseInLine(Param(0, 0, zeroIsDefault: false));
                break;
            case 'L':
                InsertLines(Param(0, 1));
                break;
            case 'M':
                DeleteLines(Param(0, 1));
                break;
            case 'P':
                DeleteCharacters(Param(0, 1));
                break;
            case 'S':
                ScrollUp(Param(0, 1));
                break;
            case 'T':
                if (_parameterCount <= 1)
                {
                    ScrollDown(Param(0, 1));
                }

                break;
            case 'X':
                EraseCells(_screen[_cursorY], _cursorX, _cursorX + Param(0, 1));
                break;
            case 'Z':
                BackTab(Param(0, 1));
                break;
            case 'b':
                for (var i = 0; i < Math.Min(Param(0, 1), 65535) && _lastPrinted != 0; i++)
                {
                    Print(_lastPrinted);
                }

                break;
            case 'c':
                if (Param(0, 0, zeroIsDefault: false) == 0)
                {
                    Respond("\u001b[?1;2c");
                }

                break;
            case 'd':
                MoveCursorTo(Param(0, 1) - 1, _cursorX);
                break;
            case 'g':
                if (Param(0, 0, zeroIsDefault: false) == 3)
                {
                    Array.Clear(_tabStops);
                }
                else
                {
                    _tabStops[Math.Min(_cursorX, Columns - 1)] = false;
                }

                break;
            case 'h':
            case 'l':
                for (var i = 0; i < Math.Max(_parameterCount, 1); i++)
                {
                    if (Param(i, 0) == 4)
                    {
                        _insertMode = final == 'h';
                    }
                }

                break;
            case 'm':
                SelectGraphicRendition();
                break;
            case 'n':
                DeviceStatusReport(Param(0, 0, zeroIsDefault: false));
                break;
            case 'r':
                SetScrollRegion(Param(0, 1) - 1, Param(1, Rows) - 1);
                break;
            case 's':
                SaveCursor();
                break;
            case 'u':
                RestoreCursor();
                break;
            default:
                break;
        }
    }

    private void DispatchCsiWithIntermediate(string intermediates, char final)
    {
        if (intermediates == " " && final == 'q' && _marker == '\0')
        {
            CursorStyle = Param(0, 0, zeroIsDefault: false);
            _dirty = true;
        }
        else if (intermediates == "!" && final == 'p')
        {
            FullReset();
        }
    }

    private void DeviceStatusReport(int kind)
    {
        if (kind == 5)
        {
            Respond("\u001b[0n");
        }
        else if (kind == 6)
        {
            var row = _cursorY - (_originMode ? _scrollTop : 0) + 1;
            Respond($"\u001b[{row};{_cursorX + 1}R");
        }
    }

    private void SetPrivateMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1:
                ApplicationCursorKeys = on;
                break;
            case 5:
                ReverseVideo = on;
                _dirty = true;
                break;
            case 6:
                _originMode = on;
                MoveCursorTo(0, 0);
                break;
            case 7:
                _autoWrap = on;
                break;
            case 25:
                CursorVisible = on;
                _dirty = true;
                break;
            case 47:
            case 1047:
                if (on)
                {
                    EnterAlternateScreen(saveCursor: false);
                }
                else
                {
                    LeaveAlternateScreen(restoreCursor: false);
                }

                break;
            case 1048:
                if (on)
                {
                    SaveCursor();
                }
                else
                {
                    RestoreCursor();
                }

                break;
            case 1049:
                if (on)
                {
                    EnterAlternateScreen(saveCursor: true);
                }
                else
                {
                    LeaveAlternateScreen(restoreCursor: true);
                }

                break;
            case 1000:
                MouseTracking = on ? MouseTracking.Buttons : MouseTracking.None;
                break;
            case 1002:
                MouseTracking = on ? MouseTracking.Drag : MouseTracking.None;
                break;
            case 1003:
                MouseTracking = on ? MouseTracking.Any : MouseTracking.None;
                break;
            case 1004:
                FocusReporting = on;
                break;
            case 1006:
                MouseSgr = on;
                break;
            case 2004:
                BracketedPaste = on;
                break;
            default:
                break;
        }
    }

    private void SelectGraphicRendition()
    {
        if (_parameterCount == 0)
        {
            ResetAttributes();
            return;
        }

        for (var i = 0; i < _parameterCount; i++)
        {
            var code = _parameters[i] < 0 ? 0 : _parameters[i];
            switch (code)
            {
                case 0:
                    ResetAttributes();
                    break;
                case 1:
                    _attributes |= CellAttributes.Bold;
                    break;
                case 2:
                    _attributes |= CellAttributes.Faint;
                    break;
                case 3:
                    _attributes |= CellAttributes.Italic;
                    break;
                case 4:
                case 21:
                    _attributes |= CellAttributes.Underline;
                    break;
                case 5:
                case 6:
                    _attributes |= CellAttributes.Blink;
                    break;
                case 7:
                    _attributes |= CellAttributes.Inverse;
                    break;
                case 8:
                    _attributes |= CellAttributes.Hidden;
                    break;
                case 9:
                    _attributes |= CellAttributes.Strikethrough;
                    break;
                case 22:
                    _attributes &= ~(CellAttributes.Bold | CellAttributes.Faint);
                    break;
                case 23:
                    _attributes &= ~CellAttributes.Italic;
                    break;
                case 24:
                    _attributes &= ~CellAttributes.Underline;
                    break;
                case 25:
                    _attributes &= ~CellAttributes.Blink;
                    break;
                case 27:
                    _attributes &= ~CellAttributes.Inverse;
                    break;
                case 28:
                    _attributes &= ~CellAttributes.Hidden;
                    break;
                case 29:
                    _attributes &= ~CellAttributes.Strikethrough;
                    break;
                case >= 30 and <= 37:
                    _foreground = TerminalColor.FromIndex(code - 30);
                    break;
                case 38:
                    i = ExtendedColor(i, isForeground: true);
                    break;
                case 39:
                    _foreground = default;
                    break;
                case >= 40 and <= 47:
                    _background = TerminalColor.FromIndex(code - 40);
                    break;
                case 48:
                    i = ExtendedColor(i, isForeground: false);
                    break;
                case 49:
                    _background = default;
                    break;
                case >= 90 and <= 97:
                    _foreground = TerminalColor.FromIndex(code - 90 + 8);
                    break;
                case >= 100 and <= 107:
                    _background = TerminalColor.FromIndex(code - 100 + 8);
                    break;
                default:
                    break;
            }
        }
    }

    private void ResetAttributes()
    {
        _attributes = CellAttributes.None;
        _foreground = default;
        _background = default;
    }

    /// <summary>Handles 38/48 with "5;n" or "2;r;g;b" (or the colon forms, where "2" may be followed by a colour space id). Returns the last parameter used.</summary>
    private int ExtendedColor(int index, bool isForeground)
    {
        var next = index + 1;
        if (next >= _parameterCount)
        {
            return index;
        }

        var colon = _subParameter[next];
        var mode = _parameters[next];
        TerminalColor color;
        int last;
        if (mode == 5 && next + 1 < _parameterCount)
        {
            color = TerminalColor.FromIndex(Math.Max(0, _parameters[next + 1]));
            last = next + 1;
        }
        else if (mode == 2)
        {
            // "38:2::r:g:b" carries an (empty) colour space id before the components.
            var first = colon && next + 4 < _parameterCount ? next + 2 : next + 1;
            if (first + 2 >= _parameterCount)
            {
                return _parameterCount - 1;
            }

            color = TerminalColor.FromRgb(Clamp(_parameters[first]), Clamp(_parameters[first + 1]), Clamp(_parameters[first + 2]));
            last = first + 2;
        }
        else
        {
            return index;
        }

        if (isForeground)
        {
            _foreground = color;
        }
        else
        {
            _background = color;
        }

        return last;
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    /// <summary>
    /// Adds one intermediate byte, refusing to grow the buffer without bound.
    /// </summary>
    /// <remarks>
    /// The sequence is terminated by the next final byte, and an unterminated one can be as long as the sender likes:
    /// a program on the other computer printing ESC [ followed by an endless run of 0x20-0x2F would otherwise grow
    /// this string with every byte and take the controller's heap with it. Past the cap the sequence is treated like
    /// any other malformed one: ignored until its final byte.
    /// </remarks>
    private bool TryAppendIntermediate(byte b)
    {
        if (_intermediates.Length >= MaxStringLength)
        {
            _state = State.CsiIgnore;
            return false;
        }

        _intermediates.Append((char)b);
        return true;
    }
}
