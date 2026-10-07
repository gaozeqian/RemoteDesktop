using System.Text;
using RemoteDesktop.Features.Terminal.Emulation;

namespace RemoteDesktop.Core.UnitTests;

public sealed class TerminalEmulatorTests
{
    private const string Esc = "\u001b";

    private static TerminalEmulator Screen(int columns = 20, int rows = 5, int scrollback = 100) => new(columns, rows, scrollback);

    private static void Feed(TerminalEmulator terminal, string text) => terminal.Feed(Encoding.UTF8.GetBytes(text));

    private static string Row(TerminalEmulator terminal, int y) => terminal.ScreenText().Split('\n')[y];

    [Fact]
    public void Text_lands_at_the_cursor_and_lines_break_with_cr_lf()
    {
        var t = Screen();
        Feed(t, "hello\r\nworld");

        Assert.Equal("hello", Row(t, 0));
        Assert.Equal("world", Row(t, 1));
        Assert.Equal((5, 1), (t.CursorColumn, t.CursorRow));
    }

    [Fact]
    public void A_line_wraps_at_the_right_edge_only_when_the_next_character_arrives()
    {
        var t = Screen(columns: 5);
        Feed(t, "12345");
        Assert.Equal((4, 0), (t.CursorColumn, t.CursorRow)); // waiting at the last column

        Feed(t, "6");
        Assert.Equal("12345", Row(t, 0));
        Assert.Equal("6", Row(t, 1));
        Assert.True(t.GetLine(t.ScrollbackCount + 1).Wrapped);
    }

    [Fact]
    public void Cursor_movement_and_absolute_positioning_work_and_stay_on_the_screen()
    {
        var t = Screen();
        Feed(t, $"{Esc}[3;4HX{Esc}[2AY{Esc}[100CZ{Esc}[1;1HA{Esc}[99;99HB");

        Assert.StartsWith("A   Y", Row(t, 0), StringComparison.Ordinal);
        Assert.Equal('Z', t.GetLine(t.ScrollbackCount + 0)[19].Character);
        Assert.Equal('X', t.GetLine(t.ScrollbackCount + 2)[3].Character);
        Assert.Equal('Y', t.GetLine(t.ScrollbackCount + 0)[4].Character);
        Assert.Equal('B', t.GetLine(t.ScrollbackCount + 4)[19].Character);
    }

    [Fact]
    public void Erasing_clears_the_requested_part_with_the_current_background()
    {
        var t = Screen(columns: 10, rows: 3);
        Feed(t, "aaaaaaaaaa\r\nbbbbbbbbbb\r\ncccccccccc");
        Feed(t, $"{Esc}[2;5H{Esc}[K"); // erase to end of line
        Assert.Equal("bbbb", Row(t, 1));

        Feed(t, $"{Esc}[2;3H{Esc}[1K"); // erase from start to cursor
        Assert.Equal("   ", Row(t, 1).Substring(0, 3));

        Feed(t, $"{Esc}[44m{Esc}[3;1H{Esc}[2K");
        Assert.Equal(TerminalColor.FromIndex(4), t.GetLine(t.ScrollbackCount + 2)[0].Background);

        Feed(t, $"{Esc}[2J");
        Assert.All(t.ScreenText().Split('\n'), line => Assert.Equal(string.Empty, line));
    }

    [Fact]
    public void Colours_in_all_three_forms_and_attributes_are_recorded_per_cell()
    {
        var t = Screen();
        Feed(t, $"{Esc}[1;31mA{Esc}[0m{Esc}[38;5;200mB{Esc}[38;2;10;20;30;48;2;1;2;3mC{Esc}[38:2::9:8:7mD{Esc}[97;4;7mE{Esc}[mF");
        var line = t.GetLine(t.ScrollbackCount);

        Assert.Equal(TerminalColor.FromIndex(1), line[0].Foreground);
        Assert.True(line[0].Flags.HasFlag(CellAttributes.Bold));
        Assert.True(line[1].Foreground.IsIndexed && line[1].Foreground.Index == 200);
        Assert.False(line[1].Flags.HasFlag(CellAttributes.Bold));
        Assert.Equal(TerminalColor.FromRgb(10, 20, 30), line[2].Foreground);
        Assert.Equal(TerminalColor.FromRgb(1, 2, 3), line[2].Background);
        Assert.Equal(TerminalColor.FromRgb(9, 8, 7), line[3].Foreground);
        Assert.Equal(TerminalColor.FromIndex(15), line[4].Foreground); // bright white
        Assert.True(line[4].Flags.HasFlag(CellAttributes.Underline | CellAttributes.Inverse));
        Assert.True(line[5].Foreground.IsDefault && line[5].Flags == CellAttributes.None);
    }

    [Fact]
    public void Lines_that_scroll_off_the_top_are_kept_in_the_scrollback()
    {
        var t = Screen(columns: 10, rows: 3);
        Feed(t, "one\r\ntwo\r\nthree\r\nfour\r\nfive");

        Assert.Equal(2, t.ScrollbackCount);
        Assert.Equal("one", RowText(t, 0));
        Assert.Equal("two", RowText(t, 1));
        Assert.Equal("three\nfour\nfive", t.ScreenText());
    }

    [Fact]
    public void A_scroll_region_scrolls_on_its_own_and_keeps_the_lines_outside()
    {
        var t = Screen(columns: 10, rows: 5);
        Feed(t, $"{Esc}[1;1Hheader{Esc}[5;1Hfooter{Esc}[2;4r{Esc}[2;1Ha\r\nb\r\nc\r\nd");

        var rows = t.ScreenText().Split('\n');
        Assert.Equal("header", rows[0]);
        Assert.Equal(["b", "c", "d"], rows[1..4]);
        Assert.Equal("footer", rows[4]);
        Assert.Equal(0, t.ScrollbackCount); // a region below the top does not feed the scrollback
    }

    [Fact]
    public void The_alternate_screen_leaves_the_normal_one_and_the_cursor_untouched()
    {
        var t = Screen(columns: 10, rows: 3);
        Feed(t, "shell text");
        var (x, y) = (t.CursorColumn, t.CursorRow);

        Feed(t, $"{Esc}[?1049h{Esc}[2J{Esc}[1;1Hvim screen");
        Assert.True(t.IsAlternateScreen);
        Assert.Equal("vim screen", Row(t, 0));
        Assert.Equal(0, t.ScrollbackCount);

        Feed(t, $"{Esc}[?1049l");
        Assert.False(t.IsAlternateScreen);
        Assert.Equal("shell text", Row(t, 0));
        Assert.Equal((x, y), (t.CursorColumn, t.CursorRow));
    }

    [Fact]
    public void Wide_characters_take_two_cells_and_do_not_split_across_the_edge()
    {
        var t = Screen(columns: 5, rows: 3);
        Feed(t, "a你好b");
        var line = t.GetLine(t.ScrollbackCount);

        Assert.Equal('你', line[1].Character);
        Assert.True(line[1].Flags.HasFlag(CellAttributes.Wide));
        Assert.True(line[2].Flags.HasFlag(CellAttributes.WideTrail));
        Assert.Equal('好', line[3].Character);
        Assert.Equal("a你好", Row(t, 0));
        Assert.Equal("b", Row(t, 1)); // the next cell would have been the last column of a wrapped pair

        var narrow = Screen(columns: 4, rows: 3);
        Feed(narrow, "abc你");
        Assert.Equal("abc", Row(narrow, 0));
        Assert.Equal("你", Row(narrow, 1));
    }

    [Fact]
    public void A_dingbat_takes_one_cell_and_comes_back_out_of_the_screen()
    {
        // U+2713 is East Asian Width "ambiguous", so it is narrow here: one cell, and it survives the round trip
        // through the screen. Pinned here rather than in the real-shell test, where whether the character survives
        // at all depends on the host console's code page and not on anything this side does.
        var t = Screen(columns: 20, rows: 3);
        Feed(t, "héllo 你好 ✓");

        Assert.Equal('✓', t.GetLine(t.ScrollbackCount)[11].Character);
        Assert.Equal("héllo 你好 ✓", Row(t, 0));
    }

    [Fact]
    public void Utf8_and_escape_sequences_may_be_split_between_feeds()
    {
        var t = Screen();
        var bytes = Encoding.UTF8.GetBytes($"€{Esc}[31mx");
        foreach (var b in bytes)
        {
            t.Feed([b]);
        }

        Assert.Equal("€x", Row(t, 0));
        Assert.Equal(TerminalColor.FromIndex(1), t.GetLine(t.ScrollbackCount)[1].Foreground);

        t.Feed([0xE2, 0x82]); // incomplete
        t.Feed(Encoding.UTF8.GetBytes("Z"));
        Assert.Contains('�', Row(t, 0));
        Assert.EndsWith("Z", Row(t, 0), StringComparison.Ordinal);
    }

    [Fact]
    public void Status_and_attribute_queries_are_answered()
    {
        var t = Screen();
        var replies = new List<string>();
        t.Reply += (_, bytes) => replies.Add(Encoding.UTF8.GetString(bytes.Span));

        Feed(t, $"{Esc}[3;7H{Esc}[6n{Esc}[5n{Esc}[c{Esc}[>c");

        Assert.Equal([$"{Esc}[3;7R", $"{Esc}[0n", $"{Esc}[?1;2c", $"{Esc}[>0;10;1c"], replies);
    }

    [Fact]
    public void Private_modes_are_tracked()
    {
        var t = Screen();
        Feed(t, $"{Esc}[?1h{Esc}[?2004h{Esc}[?1004h{Esc}[?1002h{Esc}[?1006h{Esc}[?25l");
        Assert.True(t.ApplicationCursorKeys);
        Assert.True(t.BracketedPaste);
        Assert.True(t.FocusReporting);
        Assert.Equal(MouseTracking.Drag, t.MouseTracking);
        Assert.True(t.MouseSgr);
        Assert.False(t.CursorVisible);

        Feed(t, $"{Esc}[?1l{Esc}[?2004l{Esc}[?1002l{Esc}[?25h");
        Assert.False(t.ApplicationCursorKeys);
        Assert.False(t.BracketedPaste);
        Assert.Equal(MouseTracking.None, t.MouseTracking);
        Assert.True(t.CursorVisible);
    }

    [Fact]
    public void The_window_title_is_set_by_osc_0_and_2_ended_by_bel_or_st_and_other_osc_are_ignored()
    {
        var t = Screen();
        var titles = new List<string>();
        t.TitleChanged += (_, _) => titles.Add(t.Title);

        // The event is raised once per feed, with the title as it is at the end of it.
        Feed(t, $"{Esc}]0;first title\u0007");
        Feed(t, $"{Esc}]2;second{Esc}\\");
        Feed(t, $"{Esc}]52;c;SGVsbG8=\u0007{Esc}]8;;http://x\u0007visible");

        Assert.Equal(["first title", "second"], titles);
        Assert.Equal("visible", Row(t, 0));
    }

    [Fact]
    public void Lines_and_characters_can_be_inserted_and_deleted()
    {
        var t = Screen(columns: 10, rows: 4);
        Feed(t, "1\r\n2\r\n3\r\n4");
        Feed(t, $"{Esc}[2;1H{Esc}[L");
        Assert.Equal(["1", "", "2", "3"], t.ScreenText().Split('\n'));

        Feed(t, $"{Esc}[M{Esc}[M");
        Assert.Equal(["1", "3", "", ""], t.ScreenText().Split('\n'));

        Feed(t, $"{Esc}[1;1Habcdef{Esc}[1;3H{Esc}[2P");
        Assert.Equal("abef", Row(t, 0));
        Feed(t, $"{Esc}[1;2H{Esc}[2@");
        Assert.Equal("a  bef", Row(t, 0));
    }

    [Fact]
    public void Tab_stops_every_eight_columns_can_be_changed()
    {
        var t = Screen(columns: 30);
        Feed(t, "a\tb\t\tc");
        Assert.Equal('b', t.GetLine(t.ScrollbackCount)[8].Character);
        Assert.Equal('c', t.GetLine(t.ScrollbackCount)[24].Character);

        Feed(t, $"{Esc}[3g{Esc}[1;5H{Esc}H{Esc}[1;1H\t");
        Assert.Equal(4, t.CursorColumn);
    }

    [Fact]
    public void Shrinking_keeps_the_lines_near_the_cursor_and_growing_brings_the_scrolled_ones_back()
    {
        var t = Screen(columns: 10, rows: 5);
        Feed(t, "a\r\nb\r\nc\r\nd\r\ne");

        t.Resize(10, 3);
        Assert.Equal("c\nd\ne", t.ScreenText());
        Assert.Equal(2, t.ScrollbackCount);
        Assert.Equal(2, t.CursorRow);

        t.Resize(10, 5);
        Assert.Equal("a\nb\nc\nd\ne", t.ScreenText());
        Assert.Equal(0, t.ScrollbackCount);
        Assert.Equal(4, t.CursorRow);

        t.Resize(4, 5);
        Assert.Equal(4, t.Columns);
        Assert.Equal(4, t.GetLine(0).Length);
    }

    [Fact]
    public void Selected_text_joins_wrapped_lines_and_trims_blank_ends()
    {
        var t = Screen(columns: 5, rows: 4);
        Feed(t, "abcdefgh\r\nxy");

        var all = t.GetText(new TerminalPoint(t.ScrollbackCount, 0), new TerminalPoint(t.ScrollbackCount + 1, 4));
        Assert.Equal("abcdefgh", all);

        var both = t.GetText(new TerminalPoint(t.ScrollbackCount, 1), new TerminalPoint(t.ScrollbackCount + 2, 1));
        Assert.Equal("bcdefgh\nxy", both);

        var reversed = t.GetText(new TerminalPoint(t.ScrollbackCount + 2, 1), new TerminalPoint(t.ScrollbackCount, 1));
        Assert.Equal(both, reversed);
    }

    [Fact]
    public void The_line_drawing_character_set_and_repeat_work()
    {
        var t = Screen();
        Feed(t, $"{Esc}(0lqqk{Esc}(B lqk");
        Assert.Equal("┌──┐ lqk", Row(t, 0));

        Feed(t, $"\r\nx{Esc}[3b");
        Assert.Equal("xxxx", Row(t, 1));
    }

    [Fact]
    public void Save_and_restore_cursor_bring_back_position_and_attributes()
    {
        var t = Screen();
        Feed(t, $"{Esc}[2;3H{Esc}[31m{Esc}7{Esc}[1;1H{Esc}[0m{Esc}8X");
        var cell = t.GetLine(t.ScrollbackCount + 1)[2];

        Assert.Equal('X', cell.Character);
        Assert.Equal(TerminalColor.FromIndex(1), cell.Foreground);
    }

    [Fact]
    public void A_full_reset_returns_to_the_initial_state()
    {
        var t = Screen();
        Feed(t, $"{Esc}[?1049h{Esc}[?1h{Esc}[31mtext{Esc}c");

        Assert.False(t.IsAlternateScreen);
        Assert.False(t.ApplicationCursorKeys);
        Assert.Equal((0, 0), (t.CursorColumn, t.CursorRow));
        Assert.Equal(string.Empty, Row(t, 0));
    }

    [Fact]
    public void The_bell_is_reported_once_per_feed()
    {
        var t = Screen();
        var bells = 0;
        t.Bell += (_, _) => bells++;

        Feed(t, "\u0007\u0007");
        Feed(t, "no bell");

        Assert.Equal(1, bells);
    }

    [Fact]
    public void Changed_fires_when_the_screen_changed_and_not_otherwise()
    {
        var t = Screen();
        var changes = 0;
        t.Changed += (_, _) => changes++;

        Feed(t, "x");
        Feed(t, string.Empty);
        Feed(t, $"{Esc}[1;1H"); // only moves the cursor

        Assert.Equal(1, changes);
    }

    [Fact]
    public void Garbage_and_oversized_input_do_not_break_the_parser()
    {
        var t = Screen();
        var random = new Random(3);
        var noise = new byte[50_000];
        random.NextBytes(noise);
        t.Feed(noise);
        Feed(t, $"{Esc}[{string.Join(';', Enumerable.Range(0, 100))}m{Esc}[99999999999999A{Esc}]0;{new string('x', 100_000)}\u0007");

        Feed(t, $"{Esc}c{Esc}[1;1Hstill fine");
        Assert.Equal("still fine", Row(t, 0));
    }

    private static string RowText(TerminalEmulator t, int absoluteRow)
    {
        var line = t.GetLine(absoluteRow);
        return string.Concat(line.Cells.Select(c => c.Character == 0 ? ' ' : (char)c.Character)).TrimEnd();
    }
}
