using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using RemoteDesktop.Features.Terminal;
using RemoteDesktop.Features.Terminal.Emulation;

namespace RemoteDesktop.Client.Controls;

public sealed class TerminalGridEventArgs(int columns, int rows) : EventArgs
{
    public int Columns { get; } = columns;

    public int Rows { get; } = rows;
}

/// <summary>
/// Draws a <see cref="TerminalSession"/>'s screen as a grid of cells and turns keyboard, mouse and clipboard actions into
/// terminal input. Reading the screen locks the emulator, so the shell's output can keep arriving on another thread.
/// </summary>
public sealed class TerminalControl : Control
{
    private const double FontSize = 14;
    private const int MaxCachedGlyphs = 4096;

    private static readonly FontFamily Family = new("Cascadia Mono, Cascadia Code, Consolas, Courier New");

    private readonly Dictionary<uint, SolidColorBrush> _brushes = [];
    private readonly Dictionary<(int Character, uint Color, bool Bold, bool Italic), FormattedText> _glyphs = [];
    private readonly Typeface _regular = new(Family);
    private readonly Typeface _bold = new(Family, FontStyle.Normal, FontWeight.Bold);
    private readonly Typeface _italic = new(Family, FontStyle.Italic);
    private readonly Typeface _boldItalic = new(Family, FontStyle.Italic, FontWeight.Bold);
    private double _cellWidth;
    private double _cellHeight;
    private int _columns;
    private int _rows;
    private TerminalSession? _session;
    private int _viewOffset;
    private int _knownScrollback;
    private int _redrawQueued;
    private TerminalPoint? _selectionStart;
    private TerminalPoint? _selectionEnd;
    private bool _selecting;
    private int _reportedButton = -1;

    public TerminalControl()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        MeasureCell();
    }

    /// <summary>The grid no longer has the same number of columns and rows (the control was resized).</summary>
    public event EventHandler<TerminalGridEventArgs>? GridSizeChanged;

    public int Columns => _columns;

    public int Rows => _rows;

    public TerminalSession? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(_session, value))
            {
                return;
            }

            if (_session is not null)
            {
                _session.Emulator.Changed -= OnEmulatorChanged;
            }

            _session = value;
            _viewOffset = 0;
            _knownScrollback = 0;
            ClearSelection();
            if (value is not null)
            {
                value.Emulator.Changed += OnEmulatorChanged;
                if (_columns > 0 && (value.Emulator.Columns != _columns || value.Emulator.Rows != _rows))
                {
                    value.Resize(_columns, _rows);
                }
            }

            InvalidateVisual();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_session is not null)
        {
            _session.Emulator.Changed -= OnEmulatorChanged;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_session is not null)
        {
            _session.Emulator.Changed -= OnEmulatorChanged;
            _session.Emulator.Changed += OnEmulatorChanged;
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? _cellWidth * 80 : availableSize.Width, double.IsInfinity(availableSize.Height) ? _cellHeight * 24 : availableSize.Height);

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Math.Max(2, (int)(finalSize.Width / _cellWidth));
        var rows = Math.Max(1, (int)(finalSize.Height / _cellHeight));
        if (columns != _columns || rows != _rows)
        {
            _columns = columns;
            _rows = rows;
            _session?.Resize(columns, rows);
            GridSizeChanged?.Invoke(this, new TerminalGridEventArgs(columns, rows));
        }

        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var emulator = _session?.Emulator;
        var reverse = emulator?.ReverseVideo == true;
        var defaultForeground = reverse ? TerminalPalette.DefaultBackground : TerminalPalette.DefaultForeground;
        var defaultBackground = reverse ? TerminalPalette.DefaultForeground : TerminalPalette.DefaultBackground;
        context.FillRectangle(Brush(defaultBackground), new Rect(Bounds.Size));
        if (emulator is null)
        {
            return;
        }

        lock (emulator.SyncRoot)
        {
            // Keep looking at the same text when output pushes lines into the scrollback.
            var scrollback = emulator.ScrollbackCount;
            if (_viewOffset > 0)
            {
                _viewOffset = Math.Clamp(_viewOffset + (scrollback - _knownScrollback), 0, scrollback);
            }

            _knownScrollback = scrollback;
            var top = scrollback - _viewOffset;
            for (var y = 0; y < Math.Min(emulator.Rows, _rows + 1); y++)
            {
                DrawRow(context, emulator, emulator.GetLine(top + y), y, defaultForeground, defaultBackground);
            }

            DrawSelection(context, emulator, top);
            if (_viewOffset == 0 && emulator.CursorVisible)
            {
                DrawCursor(context, emulator, defaultForeground);
            }

            if (_viewOffset > 0)
            {
                DrawScrollIndicator(context, emulator);
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var session = _session;
        if (session is null)
        {
            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        // Local actions first: copying and pasting here, and looking back through the scrollback.
        if ((control && shift && e.Key == Key.C) || (control && e.Key == Key.Insert) || (control && !shift && !alt && e.Key == Key.C && _selectionStart is not null))
        {
            _ = CopySelectionAsync();
            e.Handled = true;
            return;
        }

        if ((control && e.Key == Key.V && !alt) || (shift && e.Key == Key.Insert))
        {
            _ = PasteAsync();
            e.Handled = true;
            return;
        }

        if (shift && e.Key is Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            ScrollView(e.Key switch { Key.PageUp => _rows - 1, Key.PageDown => -(_rows - 1), Key.Home => int.MaxValue, _ => int.MinValue });
            e.Handled = true;
            return;
        }

        var modifiers = ToModifiers(e.KeyModifiers);
        if (SpecialKey(e.Key) is { } special)
        {
            ScrollToBottom();
            session.SendKey(special, modifiers);
            e.Handled = true;
            return;
        }

        // AltGr arrives as Ctrl+Alt and produces text; everything else with Ctrl or Alt is a terminal shortcut.
        if ((control || alt) && !(control && alt))
        {
            var character = e.KeySymbol is { Length: 1 } symbol && !control ? symbol[0] : CharacterOf(e.Key, shift);
            if (character != '\0' && session.SendControl(character, modifiers & ~TerminalModifiers.Shift))
            {
                ScrollToBottom();
                e.Handled = true;
            }
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        var text = e.Text;
        if (_session is null || string.IsNullOrEmpty(text) || text.All(c => c < ' ' || c == '\u007f'))
        {
            return;
        }

        ScrollToBottom();
        _session.SendText(text);
        e.Handled = true;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _session?.SendFocus(true);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _session?.SendFocus(false);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var session = _session;
        if (session is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        var (column, row) = CellAt(point.Position);
        var modifiers = ToModifiers(e.KeyModifiers);
        var button = point.Properties.IsLeftButtonPressed ? 0 : point.Properties.IsMiddleButtonPressed ? 1 : 2;
        if (!modifiers.HasFlag(TerminalModifiers.Shift) && session.SendMouse(MouseAction.Press, button, column, row, modifiers))
        {
            _reportedButton = button;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            if (_selectionStart is not null)
            {
                _ = CopySelectionAsync();
            }
            else
            {
                _ = PasteAsync();
            }

            e.Handled = true;
        }
        else if (point.Properties.IsLeftButtonPressed)
        {
            var absolute = new TerminalPoint(AbsoluteRow(row), column);
            if (e.ClickCount >= 3)
            {
                _selectionStart = new TerminalPoint(absolute.Row, 0);
                _selectionEnd = new TerminalPoint(absolute.Row, Math.Max(0, session.Emulator.Columns - 1));
            }
            else if (e.ClickCount == 2)
            {
                (_selectionStart, _selectionEnd) = WordAt(absolute);
            }
            else
            {
                _selectionStart = absolute;
                _selectionEnd = absolute;
                _selecting = true;
                e.Pointer.Capture(this);
            }

            InvalidateVisual();
            e.Handled = true;
        }
        else if (point.Properties.IsMiddleButtonPressed)
        {
            _ = PasteAsync();
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var session = _session;
        if (session is null)
        {
            return;
        }

        var (column, row) = CellAt(e.GetPosition(this));
        if (_selecting)
        {
            _selectionEnd = new TerminalPoint(AbsoluteRow(row), column);
            InvalidateVisual();
            e.Handled = true;
        }
        else if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            session.SendMouse(MouseAction.Move, _reportedButton, column, row, ToModifiers(e.KeyModifiers));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_reportedButton >= 0)
        {
            var (column, row) = CellAt(e.GetPosition(this));
            _session?.SendMouse(MouseAction.Release, _reportedButton, column, row, ToModifiers(e.KeyModifiers));
            _reportedButton = -1;
            e.Pointer.Capture(null);
            return;
        }

        if (_selecting)
        {
            _selecting = false;
            e.Pointer.Capture(null);
            if (_selectionStart == _selectionEnd)
            {
                ClearSelection();
            }

            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var session = _session;
        if (session is null)
        {
            return;
        }

        var lines = (int)Math.Round(e.Delta.Y * 3);
        if (lines == 0)
        {
            return;
        }

        var (column, row) = CellAt(e.GetPosition(this));
        var modifiers = ToModifiers(e.KeyModifiers);
        var action = lines > 0 ? MouseAction.WheelUp : MouseAction.WheelDown;
        if (!modifiers.HasFlag(TerminalModifiers.Shift) && session.SendMouse(action, 0, column, row, modifiers))
        {
            // The program handles the wheel itself (vim, htop, less with the mouse on).
        }
        else if (session.Emulator.IsAlternateScreen && !modifiers.HasFlag(TerminalModifiers.Shift))
        {
            // Full-screen programs without mouse support scroll with the arrow keys, as other terminals do.
            for (var i = 0; i < Math.Abs(lines); i++)
            {
                session.SendKey(lines > 0 ? TerminalKey.Up : TerminalKey.Down);
            }
        }
        else
        {
            ScrollView(lines);
        }

        e.Handled = true;
    }

    private static TerminalModifiers ToModifiers(KeyModifiers modifiers) =>
        (modifiers.HasFlag(KeyModifiers.Shift) ? TerminalModifiers.Shift : 0) |
        (modifiers.HasFlag(KeyModifiers.Alt) ? TerminalModifiers.Alt : 0) |
        (modifiers.HasFlag(KeyModifiers.Control) ? TerminalModifiers.Control : 0);

    private static TerminalKey? SpecialKey(Key key) => key switch
    {
        Key.Enter => TerminalKey.Enter,
        Key.Tab => TerminalKey.Tab,
        Key.Back => TerminalKey.Backspace,
        Key.Escape => TerminalKey.Escape,
        Key.Up => TerminalKey.Up,
        Key.Down => TerminalKey.Down,
        Key.Left => TerminalKey.Left,
        Key.Right => TerminalKey.Right,
        Key.Home => TerminalKey.Home,
        Key.End => TerminalKey.End,
        Key.PageUp => TerminalKey.PageUp,
        Key.PageDown => TerminalKey.PageDown,
        Key.Insert => TerminalKey.Insert,
        Key.Delete => TerminalKey.Delete,
        >= Key.F1 and <= Key.F12 => TerminalKey.F1 + (key - Key.F1),
        _ => null,
    };

    // The character a key stands for when it is combined with Ctrl or Alt (the text input event does not fire for those).
    private static char CharacterOf(Key key, bool shift) => key switch
    {
        >= Key.A and <= Key.Z => (char)((shift ? 'A' : 'a') + (key - Key.A)),
        >= Key.D0 and <= Key.D9 => (char)('0' + (key - Key.D0)),
        >= Key.NumPad0 and <= Key.NumPad9 => (char)('0' + (key - Key.NumPad0)),
        Key.Space => ' ',
        Key.OemOpenBrackets => '[',
        Key.OemCloseBrackets => ']',
        Key.OemPipe or Key.OemBackslash => '\\',
        Key.OemMinus => '-',
        Key.OemQuestion => '/',
        Key.OemComma => ',',
        Key.OemPeriod => '.',
        Key.OemSemicolon => ';',
        Key.OemQuotes => '\'',
        Key.OemPlus => '=',
        Key.OemTilde => '`',
        _ => '\0',
    };

    private void MeasureCell()
    {
        var sample = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _regular, FontSize, Brushes.White);
        _cellWidth = Math.Max(1, sample.Width / 10);
        _cellHeight = Math.Max(1, Math.Ceiling(sample.Height));
    }

    private (int Column, int Row) CellAt(Point position) => (
        Math.Clamp((int)(position.X / _cellWidth), 0, Math.Max(0, _columns - 1)),
        Math.Clamp((int)(position.Y / _cellHeight), 0, Math.Max(0, _rows - 1)));

    private int AbsoluteRow(int viewRow) => (_session?.Emulator.ScrollbackCount ?? 0) - _viewOffset + viewRow;

    private void ScrollView(int lines)
    {
        var scrollback = _session?.Emulator.ScrollbackCount ?? 0;
        _viewOffset = (int)Math.Clamp((long)_viewOffset + lines, 0, scrollback);
        InvalidateVisual();
    }

    private void ScrollToBottom()
    {
        if (_viewOffset != 0)
        {
            _viewOffset = 0;
            InvalidateVisual();
        }
    }

    private void OnEmulatorChanged(object? sender, EventArgs e)
    {
        // Output arrives in bursts from a background thread; draw at most once per UI turn.
        if (Interlocked.Exchange(ref _redrawQueued, 1) == 0)
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    Volatile.Write(ref _redrawQueued, 0);
                    InvalidateVisual();
                },
                DispatcherPriority.Render);
        }
    }

    private void ClearSelection()
    {
        _selectionStart = null;
        _selectionEnd = null;
        _selecting = false;
    }

    private (TerminalPoint Start, TerminalPoint End) WordAt(TerminalPoint point)
    {
        var emulator = _session!.Emulator;
        lock (emulator.SyncRoot)
        {
            var line = emulator.GetLine(Math.Clamp(point.Row, 0, emulator.TotalLines - 1));
            bool IsWordChar(int column) => line[column].Character is > ' ' and not 0x7F;
            if (point.Column >= line.Length || !IsWordChar(point.Column))
            {
                return (point, point);
            }

            var start = point.Column;
            var end = point.Column;
            while (start > 0 && IsWordChar(start - 1))
            {
                start--;
            }

            while (end < line.Length - 1 && IsWordChar(end + 1))
            {
                end++;
            }

            return (new TerminalPoint(point.Row, start), new TerminalPoint(point.Row, end));
        }
    }

    private async Task CopySelectionAsync()
    {
        if (_session is null || _selectionStart is not { } start || _selectionEnd is not { } end)
        {
            return;
        }

        var text = _session.Emulator.GetText(start, end);
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && text.Length > 0)
        {
            await clipboard.SetTextAsync(text);
        }

        ClearSelection();
        InvalidateVisual();
    }

    private async Task PasteAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && await clipboard.TryGetTextAsync() is { Length: > 0 } text)
        {
            ScrollToBottom();
            _session?.Paste(text);
        }
    }

    private SolidColorBrush Brush(Color color)
    {
        var key = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        if (!_brushes.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(color);
            _brushes[key] = brush;
        }

        return brush;
    }

    private void DrawRow(DrawingContext context, TerminalEmulator emulator, TerminalLine line, int y, Color defaultForeground, Color defaultBackground)
    {
        var top = y * _cellHeight;
        var columns = Math.Min(line.Length, Math.Min(emulator.Columns, _columns + 1));
        var text = new System.Text.StringBuilder();
        var runStart = 0;
        var runStyle = default(RunStyle);

        void FlushRun(int endColumn)
        {
            if (text.Length > 0)
            {
                DrawText(context, text.ToString(), runStart, top, runStyle);
                text.Clear();
            }
        }

        for (var x = 0; x < columns; x++)
        {
            var cell = line.Cells[x];
            if ((cell.Flags & CellAttributes.WideTrail) != 0)
            {
                continue;
            }

            var style = StyleOf(cell, defaultForeground, defaultBackground);
            var wide = (cell.Flags & CellAttributes.Wide) != 0;
            if (style.Background != defaultBackground)
            {
                context.FillRectangle(Brush(style.Background), new Rect(x * _cellWidth, top, (wide ? 2 : 1) * _cellWidth + 0.5, _cellHeight));
            }

            var character = cell.Character == 0 ? ' ' : cell.Character;
            if (wide || character > 0x7E)
            {
                // Glyphs outside ASCII may come from a fallback font with another advance: each sits in its own cell.
                FlushRun(x);
                if (character != ' ' && !style.Hidden)
                {
                    DrawGlyph(context, character, x, top, style);
                }

                DrawDecorations(context, x, top, wide ? 2 : 1, style);
                continue;
            }

            if (text.Length > 0 && !style.SameTextAs(runStyle))
            {
                FlushRun(x);
            }

            if (text.Length == 0)
            {
                runStart = x;
                runStyle = style;
            }

            text.Append((char)character);
        }

        FlushRun(columns);
    }

    private void DrawText(DrawingContext context, string text, int startColumn, double top, RunStyle style)
    {
        if (!style.Hidden && text.Any(c => c != ' '))
        {
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TypefaceOf(style), FontSize, Brush(style.Foreground));
            context.DrawText(formatted, new Point(startColumn * _cellWidth, top + ((_cellHeight - formatted.Height) / 2)));
        }

        DrawDecorations(context, startColumn, top, text.Length, style);
    }

    private void DrawGlyph(DrawingContext context, int character, int column, double top, RunStyle style)
    {
        var key = (character, style.Foreground.ToUInt32(), style.Bold, style.Italic);
        if (!_glyphs.TryGetValue(key, out var formatted))
        {
            if (_glyphs.Count >= MaxCachedGlyphs)
            {
                _glyphs.Clear();
            }

            formatted = new FormattedText(char.ConvertFromUtf32(character), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TypefaceOf(style), FontSize, Brush(style.Foreground));
            _glyphs[key] = formatted;
        }

        context.DrawText(formatted, new Point(column * _cellWidth, top + ((_cellHeight - formatted.Height) / 2)));
    }

    private void DrawDecorations(DrawingContext context, int column, double top, int cells, RunStyle style)
    {
        if (style.Underline)
        {
            var yLine = top + _cellHeight - 2;
            context.DrawLine(new Pen(Brush(style.Foreground), 1), new Point(column * _cellWidth, yLine), new Point((column + cells) * _cellWidth, yLine));
        }

        if (style.Strikethrough)
        {
            var yLine = top + (_cellHeight / 2);
            context.DrawLine(new Pen(Brush(style.Foreground), 1), new Point(column * _cellWidth, yLine), new Point((column + cells) * _cellWidth, yLine));
        }
    }

    private Typeface TypefaceOf(RunStyle style) => style.Bold ? (style.Italic ? _boldItalic : _bold) : (style.Italic ? _italic : _regular);

    private static RunStyle StyleOf(TerminalCell cell, Color defaultForeground, Color defaultBackground)
    {
        var foreground = TerminalPalette.Resolve(cell.Foreground, defaultForeground);
        var background = TerminalPalette.Resolve(cell.Background, defaultBackground);
        if ((cell.Flags & CellAttributes.Inverse) != 0)
        {
            (foreground, background) = (background, foreground);
        }

        if ((cell.Flags & CellAttributes.Faint) != 0)
        {
            foreground = TerminalPalette.Blend(foreground, background, 0.5);
        }

        return new RunStyle(
            foreground,
            background,
            (cell.Flags & CellAttributes.Bold) != 0,
            (cell.Flags & CellAttributes.Italic) != 0,
            (cell.Flags & CellAttributes.Underline) != 0,
            (cell.Flags & CellAttributes.Strikethrough) != 0,
            (cell.Flags & CellAttributes.Hidden) != 0);
    }

    private void DrawSelection(DrawingContext context, TerminalEmulator emulator, int topRow)
    {
        if (_selectionStart is not { } a || _selectionEnd is not { } b)
        {
            return;
        }

        var (start, end) = a <= b ? (a, b) : (b, a);
        var brush = new SolidColorBrush(Color.FromArgb(0x70, 0x3A, 0x96, 0xDD));
        for (var y = 0; y < Math.Min(emulator.Rows, _rows + 1); y++)
        {
            var row = topRow + y;
            if (row < start.Row || row > end.Row)
            {
                continue;
            }

            var from = row == start.Row ? start.Column : 0;
            var to = row == end.Row ? end.Column : emulator.Columns - 1;
            context.FillRectangle(brush, new Rect(from * _cellWidth, y * _cellHeight, (to - from + 1) * _cellWidth, _cellHeight));
        }
    }

    private void DrawCursor(DrawingContext context, TerminalEmulator emulator, Color foreground)
    {
        var x = emulator.CursorColumn * _cellWidth;
        var y = emulator.CursorRow * _cellHeight;
        var brush = Brush(foreground);
        var style = emulator.CursorStyle;
        if (!IsFocused)
        {
            context.DrawRectangle(null, new Pen(brush, 1), new Rect(x + 0.5, y + 0.5, _cellWidth - 1, _cellHeight - 1));
        }
        else if (style is 3 or 4)
        {
            context.FillRectangle(brush, new Rect(x, y + _cellHeight - 2, _cellWidth, 2));
        }
        else if (style is 5 or 6)
        {
            context.FillRectangle(brush, new Rect(x, y, 2, _cellHeight));
        }
        else
        {
            context.FillRectangle(new SolidColorBrush(foreground, 0.65), new Rect(x, y, _cellWidth, _cellHeight));
        }
    }

    private void DrawScrollIndicator(DrawingContext context, TerminalEmulator emulator)
    {
        var total = emulator.TotalLines;
        var height = Bounds.Height;
        var thumb = Math.Max(20, height * emulator.Rows / total);
        var position = (height - thumb) * (emulator.ScrollbackCount - _viewOffset) / Math.Max(1, emulator.ScrollbackCount);
        context.FillRectangle(new SolidColorBrush(Colors.White, 0.35), new Rect(Bounds.Width - 6, position, 4, thumb));
    }

    private readonly record struct RunStyle(Color Foreground, Color Background, bool Bold, bool Italic, bool Underline, bool Strikethrough, bool Hidden)
    {
        public bool SameTextAs(RunStyle other) => Foreground == other.Foreground && Background == other.Background && Bold == other.Bold && Italic == other.Italic
            && Underline == other.Underline && Strikethrough == other.Strikethrough && Hidden == other.Hidden;
    }
}
