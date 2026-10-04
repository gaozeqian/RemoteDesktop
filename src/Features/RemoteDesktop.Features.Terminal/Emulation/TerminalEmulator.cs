using System.Text;

namespace RemoteDesktop.Features.Terminal.Emulation;

public enum MouseTracking
{
    None,

    /// <summary>Button presses and releases (xterm 1000).</summary>
    Buttons,

    /// <summary>Also movement while a button is held (1002).</summary>
    Drag,

    /// <summary>All movement (1003).</summary>
    Any,
}

/// <summary>Where a text selection starts or ends: a row counted from the oldest scrollback line, and a column.</summary>
public readonly record struct TerminalPoint(int Row, int Column) : IComparable<TerminalPoint>
{
    public int CompareTo(TerminalPoint other) => Row != other.Row ? Row.CompareTo(other.Row) : Column.CompareTo(other.Column);

    public static bool operator <(TerminalPoint left, TerminalPoint right) => left.CompareTo(right) < 0;

    public static bool operator >(TerminalPoint left, TerminalPoint right) => left.CompareTo(right) > 0;

    public static bool operator <=(TerminalPoint left, TerminalPoint right) => left.CompareTo(right) <= 0;

    public static bool operator >=(TerminalPoint left, TerminalPoint right) => left.CompareTo(right) >= 0;
}

/// <summary>
/// A terminal screen that understands the escape sequences of an xterm-compatible terminal (what ConPTY, PowerShell, vim
/// and htop speak): cursor movement, colours (16, 256 and 24-bit), scrolling regions, the alternate screen, scrollback,
/// wide characters, mouse and paste modes. Bytes go in through <see cref="Feed"/>; the renderer reads the lines.
/// </summary>
/// <remarks>
/// All members that touch the screen take <see cref="SyncRoot"/>; a renderer running on another thread than the feeder
/// locks it while it reads. <see cref="Changed"/> and the other events fire outside of the lock.
/// </remarks>
public sealed partial class TerminalEmulator
{
    public const int DefaultScrollback = 5000;

    private const int MaxColumns = 1000;
    private const int MaxRows = 500;

    private readonly ScrollbackBuffer _scrollback;
    private TerminalLine[] _primary;
    private TerminalLine[] _alternate;
    private TerminalLine[] _screen;
    private bool _alternateActive;
    private bool[] _tabStops;

    private int _cursorX;
    private int _cursorY;
    private bool _wrapPending;
    private TerminalColor _foreground;
    private TerminalColor _background;
    private CellAttributes _attributes;
    private int _scrollTop;
    private int _scrollBottom;
    private bool _autoWrap = true;
    private bool _originMode;
    private bool _insertMode;
    private bool _dirty;
    private int _lastPrinted;

    private SavedCursor _savedCursor;
    private SavedCursor _savedCursorPrimary;

    public TerminalEmulator(int columns, int rows, int scrollbackLines = DefaultScrollback)
    {
        Columns = Math.Clamp(columns, 1, MaxColumns);
        Rows = Math.Clamp(rows, 1, MaxRows);
        _scrollback = new ScrollbackBuffer(scrollbackLines);
        _primary = NewScreen();
        _alternate = NewScreen();
        _screen = _primary;
        _scrollBottom = Rows - 1;
        _tabStops = DefaultTabStops(Columns);
        _savedCursor = SavedCursor.Initial;
        _savedCursorPrimary = SavedCursor.Initial;
    }

    /// <summary>Anything that reads the screen while another thread feeds it must hold this.</summary>
    public object SyncRoot { get; } = new();

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    /// <summary>Lines above the visible screen.</summary>
    public int ScrollbackCount => _scrollback.Count;

    /// <summary>Scrollback plus screen.</summary>
    public int TotalLines => ScrollbackCount + Rows;

    public int CursorColumn => _cursorX;

    public int CursorRow => _cursorY;

    public bool CursorVisible { get; private set; } = true;

    /// <summary>DECSCUSR: 0/1 blinking block, 2 block, 3/4 underline, 5/6 bar.</summary>
    public int CursorStyle { get; private set; }

    public bool IsAlternateScreen => _alternateActive;

    public bool ApplicationCursorKeys { get; private set; }

    public bool ApplicationKeypad { get; private set; }

    public bool BracketedPaste { get; private set; }

    public bool FocusReporting { get; private set; }

    public bool ReverseVideo { get; private set; }

    public MouseTracking MouseTracking { get; private set; }

    /// <summary>Mouse reports use the SGR encoding (1006), which is the only one supported for large screens.</summary>
    public bool MouseSgr { get; private set; }

    public string Title { get; private set; } = string.Empty;

    /// <summary>The screen (or its title) changed; coalesced to one event per <see cref="Feed"/>.</summary>
    public event EventHandler? Changed;

    public event EventHandler? TitleChanged;

    public event EventHandler? Bell;

    /// <summary>Bytes the terminal answers with (cursor position reports, device attributes); they belong to the shell's input.</summary>
    public event EventHandler<ReadOnlyMemory<byte>>? Reply;

    /// <summary>A line by absolute row: 0 is the oldest scrollback line, <see cref="ScrollbackCount"/> the top of the screen.</summary>
    public TerminalLine GetLine(int row) => row < _scrollback.Count ? _scrollback[row] : _screen[Math.Clamp(row - _scrollback.Count, 0, Rows - 1)];

    public void Feed(ReadOnlySpan<byte> data)
    {
        var titleChanged = false;
        var bell = false;
        var previousTitle = Title;
        lock (SyncRoot)
        {
            _bellRaised = false;
            foreach (var b in data)
            {
                Process(b);
            }

            titleChanged = !string.Equals(previousTitle, Title, StringComparison.Ordinal);
            bell = _bellRaised;
        }

        if (titleChanged)
        {
            TitleChanged?.Invoke(this, EventArgs.Empty);
        }

        if (bell)
        {
            Bell?.Invoke(this, EventArgs.Empty);
        }

        if (_dirty)
        {
            _dirty = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Changes the size, keeping the content (no reflow). Lines that no longer fit at the top go to the scrollback.</summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 1, MaxColumns);
        rows = Math.Clamp(rows, 1, MaxRows);
        lock (SyncRoot)
        {
            if (columns == Columns && rows == Rows)
            {
                return;
            }

            foreach (var line in _primary.Concat(_alternate))
            {
                line.Resize(columns);
            }

            for (var i = 0; i < _scrollback.Count; i++)
            {
                _scrollback[i].Resize(columns);
            }

            _primary = ResizeRows(_primary, rows, columns, isPrimary: true);
            _alternate = ResizeRows(_alternate, rows, columns, isPrimary: false);
            _screen = _alternateActive ? _alternate : _primary;
            Columns = columns;
            Rows = rows;
            _scrollTop = 0;
            _scrollBottom = rows - 1;
            _cursorX = Math.Min(_cursorX, columns - 1);
            _cursorY = Math.Clamp(_cursorY, 0, rows - 1);
            _wrapPending = false;
            _tabStops = DefaultTabStops(columns);
            _dirty = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        _dirty = false;
    }

    /// <summary>The text between two points (inclusive), as it would be copied: wrapped rows are joined, trailing blanks dropped.</summary>
    public string GetText(TerminalPoint from, TerminalPoint to)
    {
        if (to.CompareTo(from) < 0)
        {
            (from, to) = (to, from);
        }

        var builder = new StringBuilder();
        lock (SyncRoot)
        {
            var last = TotalLines - 1;
            for (var row = Math.Max(0, from.Row); row <= Math.Min(to.Row, last); row++)
            {
                var line = GetLine(row);
                var start = row == from.Row ? from.Column : 0;
                var end = row == to.Row ? to.Column : line.Length - 1;
                var text = new StringBuilder();
                for (var column = Math.Max(0, start); column <= Math.Min(end, line.Length - 1); column++)
                {
                    var cell = line.Cells[column];
                    if ((cell.Flags & CellAttributes.WideTrail) == 0)
                    {
                        text.Append(cell.Character == 0 ? " " : char.ConvertFromUtf32(cell.Character));
                    }
                }

                var isSelectedToEnd = row != to.Row || end >= line.Length - 1;
                var continues = row < last && row < to.Row && GetLine(row + 1).Wrapped;
                builder.Append(continues || !isSelectedToEnd ? text.ToString() : text.ToString().TrimEnd());
                if (row < to.Row && !continues)
                {
                    builder.Append('\n');
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>The visible screen as text, one line per row with trailing blanks removed. Meant for tests and diagnostics.</summary>
    public string ScreenText()
    {
        lock (SyncRoot)
        {
            var lines = Enumerable.Range(0, Rows).Select(y => RowText(_screen[y]).TrimEnd());
            return string.Join('\n', lines);
        }
    }

    private static string RowText(TerminalLine line)
    {
        var text = new StringBuilder();
        foreach (var cell in line.Cells)
        {
            if ((cell.Flags & CellAttributes.WideTrail) == 0)
            {
                text.Append(cell.Character == 0 ? " " : char.ConvertFromUtf32(cell.Character));
            }
        }

        return text.ToString();
    }

    private static bool[] DefaultTabStops(int columns)
    {
        var stops = new bool[columns];
        for (var i = 8; i < columns; i += 8)
        {
            stops[i] = true;
        }

        return stops;
    }

    private TerminalLine[] NewScreen() => Enumerable.Range(0, Rows).Select(_ => new TerminalLine(Columns)).ToArray();

    private TerminalLine[] ResizeRows(TerminalLine[] screen, int rows, int columns, bool isPrimary)
    {
        var lines = new List<TerminalLine>(screen);
        if (rows < lines.Count)
        {
            // Lines the cursor would fall off are kept (in the scrollback) rather than lost; only empty rows below are cut.
            var overflow = lines.Count - rows;
            var followsCursor = isPrimary && !_alternateActive;
            var fromTop = followsCursor ? Math.Clamp(_cursorY - (rows - 1), 0, overflow) : 0;
            for (var i = 0; i < fromTop; i++)
            {
                _scrollback.Add(lines[0]);
                lines.RemoveAt(0);
            }

            _cursorY -= fromTop;

            lines.RemoveRange(rows, lines.Count - rows);
        }
        else
        {
            // Growing brings back lines from the scrollback first, so a shrink followed by a grow restores the view.
            while (lines.Count < rows)
            {
                if (isPrimary && !_alternateActive && _scrollback.RemoveLast() is { } restored)
                {
                    lines.Insert(0, restored);
                    _cursorY++;
                }
                else
                {
                    lines.Add(new TerminalLine(columns));
                }
            }
        }

        return [.. lines];
    }

    private TerminalCell Blank() => new() { Background = _background };

    private void EraseCells(TerminalLine line, int from, int to)
    {
        var blank = Blank();
        for (var x = Math.Max(0, from); x < Math.Min(to, line.Length); x++)
        {
            line.Cells[x] = blank;
        }

        _dirty = true;
    }

    private void ScrollUp(int count)
    {
        count = Math.Min(count, _scrollBottom - _scrollTop + 1);
        for (var i = 0; i < count; i++)
        {
            var removed = _screen[_scrollTop];
            Array.Copy(_screen, _scrollTop + 1, _screen, _scrollTop, _scrollBottom - _scrollTop);
            var fresh = new TerminalLine(Columns);
            EraseCells(fresh, 0, Columns);
            _screen[_scrollBottom] = fresh;
            if (!_alternateActive && _scrollTop == 0)
            {
                _scrollback.Add(removed);
            }
        }

        _dirty = true;
    }

    private void ScrollDown(int count)
    {
        count = Math.Min(count, _scrollBottom - _scrollTop + 1);
        for (var i = 0; i < count; i++)
        {
            Array.Copy(_screen, _scrollTop, _screen, _scrollTop + 1, _scrollBottom - _scrollTop);
            var fresh = new TerminalLine(Columns);
            EraseCells(fresh, 0, Columns);
            _screen[_scrollTop] = fresh;
        }

        _dirty = true;
    }

    private void LineFeed()
    {
        if (_cursorY == _scrollBottom)
        {
            ScrollUp(1);
        }
        else if (_cursorY < Rows - 1)
        {
            _cursorY++;
        }

        _wrapPending = false;
    }

    private void ReverseIndex()
    {
        if (_cursorY == _scrollTop)
        {
            ScrollDown(1);
        }
        else if (_cursorY > 0)
        {
            _cursorY--;
        }

        _wrapPending = false;
    }

    private void CarriageReturn()
    {
        _cursorX = 0;
        _wrapPending = false;
    }

    private void Backspace()
    {
        if (_cursorX > 0)
        {
            _cursorX--;
        }

        _wrapPending = false;
    }

    private void Tab(int count)
    {
        for (var i = 0; i < count; i++)
        {
            do
            {
                _cursorX++;
            }
            while (_cursorX < Columns - 1 && !_tabStops[_cursorX]);
        }

        _cursorX = Math.Min(_cursorX, Columns - 1);
        _wrapPending = false;
    }

    private void BackTab(int count)
    {
        for (var i = 0; i < count; i++)
        {
            do
            {
                _cursorX--;
            }
            while (_cursorX > 0 && !_tabStops[_cursorX]);
        }

        _cursorX = Math.Max(_cursorX, 0);
        _wrapPending = false;
    }

    private void MoveCursorTo(int row, int column)
    {
        var top = _originMode ? _scrollTop : 0;
        var bottom = _originMode ? _scrollBottom : Rows - 1;
        _cursorY = Math.Clamp(top + row, top, bottom);
        _cursorX = Math.Clamp(column, 0, Columns - 1);
        _wrapPending = false;
    }

    /// <summary>Writes a character at the cursor, wrapping and scrolling as the modes say.</summary>
    private void Print(int codePoint)
    {
        var width = CharWidth.Of(codePoint);
        if (width == 0)
        {
            return; // combining marks and joiners are not drawn separately
        }

        if (_wrapPending && _autoWrap)
        {
            _screen[_cursorY].Wrapped = false;
            CarriageReturn();
            LineFeed();
            _screen[_cursorY].Wrapped = true;
        }

        if (width == 2 && _cursorX == Columns - 1)
        {
            if (!_autoWrap)
            {
                return;
            }

            EraseCells(_screen[_cursorY], _cursorX, Columns);
            CarriageReturn();
            LineFeed();
            _screen[_cursorY].Wrapped = true;
        }

        var line = _screen[_cursorY];
        if (_insertMode)
        {
            var shift = width;
            Array.Copy(line.Cells, _cursorX, line.Cells, _cursorX + shift, Columns - _cursorX - shift);
        }

        ClearWideNeighbours(line, _cursorX);
        if (width == 2)
        {
            ClearWideNeighbours(line, _cursorX + 1);
        }

        line.Cells[_cursorX] = new TerminalCell
        {
            Character = codePoint,
            Foreground = _foreground,
            Background = _background,
            Flags = _attributes | (width == 2 ? CellAttributes.Wide : 0),
        };
        if (width == 2)
        {
            line.Cells[_cursorX + 1] = new TerminalCell { Background = _background, Foreground = _foreground, Flags = _attributes | CellAttributes.WideTrail };
        }

        _lastPrinted = codePoint;
        _dirty = true;
        if (_cursorX + width >= Columns)
        {
            _cursorX = Columns - 1;
            _wrapPending = true;
        }
        else
        {
            _cursorX += width;
            _wrapPending = false;
        }
    }

    // Overwriting one half of a double-width character would leave the other half meaningless.
    private static void ClearWideNeighbours(TerminalLine line, int x)
    {
        if (x >= line.Length)
        {
            return;
        }

        if ((line.Cells[x].Flags & CellAttributes.WideTrail) != 0 && x > 0)
        {
            line.Cells[x - 1] = default;
        }

        if ((line.Cells[x].Flags & CellAttributes.Wide) != 0 && x + 1 < line.Length)
        {
            line.Cells[x + 1] = default;
        }
    }

    private void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseCells(_screen[_cursorY], _cursorX, Columns);
                for (var y = _cursorY + 1; y < Rows; y++)
                {
                    EraseCells(_screen[y], 0, Columns);
                    _screen[y].Wrapped = false;
                }

                break;
            case 1:
                for (var y = 0; y < _cursorY; y++)
                {
                    EraseCells(_screen[y], 0, Columns);
                    _screen[y].Wrapped = false;
                }

                EraseCells(_screen[_cursorY], 0, _cursorX + 1);
                break;
            case 2:
                for (var y = 0; y < Rows; y++)
                {
                    EraseCells(_screen[y], 0, Columns);
                    _screen[y].Wrapped = false;
                }

                break;
            case 3:
                _scrollback.Clear();
                break;
            default:
                break;
        }

        _dirty = true;
    }

    private void EraseInLine(int mode)
    {
        var line = _screen[_cursorY];
        switch (mode)
        {
            case 0:
                EraseCells(line, _cursorX, Columns);
                break;
            case 1:
                EraseCells(line, 0, _cursorX + 1);
                break;
            case 2:
                EraseCells(line, 0, Columns);
                break;
            default:
                break;
        }
    }

    private void InsertLines(int count)
    {
        if (_cursorY < _scrollTop || _cursorY > _scrollBottom)
        {
            return;
        }

        var saveTop = _scrollTop;
        _scrollTop = _cursorY;
        ScrollDown(count);
        _scrollTop = saveTop;
        CarriageReturn();
    }

    private void DeleteLines(int count)
    {
        if (_cursorY < _scrollTop || _cursorY > _scrollBottom)
        {
            return;
        }

        var saveTop = _scrollTop;
        var saveAlternate = _alternateActive;
        _scrollTop = _cursorY;

        // Deleted lines are gone, not scrolled back.
        _alternateActive = true;
        ScrollUp(count);
        _alternateActive = saveAlternate;
        _scrollTop = saveTop;
        CarriageReturn();
    }

    private void InsertCharacters(int count)
    {
        var line = _screen[_cursorY];
        count = Math.Min(count, Columns - _cursorX);
        Array.Copy(line.Cells, _cursorX, line.Cells, _cursorX + count, Columns - _cursorX - count);
        EraseCells(line, _cursorX, _cursorX + count);
        _wrapPending = false;
    }

    private void DeleteCharacters(int count)
    {
        var line = _screen[_cursorY];
        count = Math.Min(count, Columns - _cursorX);
        Array.Copy(line.Cells, _cursorX + count, line.Cells, _cursorX, Columns - _cursorX - count);
        EraseCells(line, Columns - count, Columns);
        _wrapPending = false;
    }

    private void SetScrollRegion(int top, int bottom)
    {
        if (top < 0 || bottom >= Rows || top >= bottom)
        {
            top = 0;
            bottom = Rows - 1;
        }

        _scrollTop = top;
        _scrollBottom = bottom;
        MoveCursorTo(0, 0);
    }

    private void SaveCursor() => _savedCursor = new SavedCursor(_cursorX, _cursorY, _foreground, _background, _attributes, _originMode, _wrapPending, _lineDrawingG0, _lineDrawingG1, _shifted);

    private void RestoreCursor()
    {
        var saved = _savedCursor;
        _cursorX = Math.Min(saved.X, Columns - 1);
        _cursorY = Math.Min(saved.Y, Rows - 1);
        _foreground = saved.Foreground;
        _background = saved.Background;
        _attributes = saved.Attributes;
        _originMode = saved.OriginMode;
        _wrapPending = saved.WrapPending;
        _lineDrawingG0 = saved.LineDrawingG0;
        _lineDrawingG1 = saved.LineDrawingG1;
        _shifted = saved.Shifted;
    }

    private void EnterAlternateScreen(bool saveCursor)
    {
        if (_alternateActive)
        {
            return;
        }

        if (saveCursor)
        {
            SaveCursor();
            _savedCursorPrimary = _savedCursor;
        }

        _alternateActive = true;
        _screen = _alternate;
        foreach (var line in _screen)
        {
            EraseCells(line, 0, Columns);
            line.Wrapped = false;
        }

        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        _dirty = true;
    }

    private void LeaveAlternateScreen(bool restoreCursor)
    {
        if (!_alternateActive)
        {
            return;
        }

        _alternateActive = false;
        _screen = _primary;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        if (restoreCursor)
        {
            _savedCursor = _savedCursorPrimary;
            RestoreCursor();
        }

        _dirty = true;
    }

    private void FullReset()
    {
        LeaveAlternateScreen(restoreCursor: false);
        _foreground = default;
        _background = default;
        _attributes = CellAttributes.None;
        _autoWrap = true;
        _originMode = false;
        _insertMode = false;
        _wrapPending = false;
        _lineDrawingG0 = false;
        _lineDrawingG1 = false;
        _shifted = false;
        CursorVisible = true;
        CursorStyle = 0;
        ApplicationCursorKeys = false;
        ApplicationKeypad = false;
        BracketedPaste = false;
        FocusReporting = false;
        ReverseVideo = false;
        MouseTracking = MouseTracking.None;
        MouseSgr = false;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        _tabStops = DefaultTabStops(Columns);
        _savedCursor = SavedCursor.Initial;
        EraseInDisplay(2);
        _cursorX = 0;
        _cursorY = 0;
    }

    private void Respond(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Reply?.Invoke(this, bytes);
    }

    private readonly record struct SavedCursor(
        int X,
        int Y,
        TerminalColor Foreground,
        TerminalColor Background,
        CellAttributes Attributes,
        bool OriginMode,
        bool WrapPending,
        bool LineDrawingG0,
        bool LineDrawingG1,
        bool Shifted)
    {
        public static SavedCursor Initial => new(0, 0, default, default, CellAttributes.None, false, false, false, false, false);
    }
}
