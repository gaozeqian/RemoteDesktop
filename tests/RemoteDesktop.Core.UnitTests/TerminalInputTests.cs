using System.Text;
using RemoteDesktop.Features.Terminal.Emulation;

namespace RemoteDesktop.Core.UnitTests;

public sealed class TerminalInputTests
{
    private const string Esc = "\u001b";

    private static string Text(byte[]? bytes) => Encoding.UTF8.GetString(bytes!);

    [Theory]
    [InlineData(TerminalKey.Up, TerminalModifiers.None, false, "\u001b[A")]
    [InlineData(TerminalKey.Up, TerminalModifiers.None, true, "\u001bOA")]
    [InlineData(TerminalKey.Home, TerminalModifiers.None, false, "\u001b[H")]
    [InlineData(TerminalKey.End, TerminalModifiers.None, true, "\u001bOF")]
    [InlineData(TerminalKey.Left, TerminalModifiers.Control, false, "\u001b[1;5D")]
    [InlineData(TerminalKey.Right, TerminalModifiers.Shift | TerminalModifiers.Alt, true, "\u001b[1;4C")]
    [InlineData(TerminalKey.F1, TerminalModifiers.None, false, "\u001bOP")]
    [InlineData(TerminalKey.F4, TerminalModifiers.Shift, false, "\u001b[1;2S")]
    [InlineData(TerminalKey.F5, TerminalModifiers.None, false, "\u001b[15~")]
    [InlineData(TerminalKey.F12, TerminalModifiers.Control, false, "\u001b[24;5~")]
    [InlineData(TerminalKey.Delete, TerminalModifiers.None, false, "\u001b[3~")]
    [InlineData(TerminalKey.PageDown, TerminalModifiers.None, false, "\u001b[6~")]
    [InlineData(TerminalKey.Insert, TerminalModifiers.Shift, false, "\u001b[2;2~")]
    [InlineData(TerminalKey.Enter, TerminalModifiers.None, false, "\r")]
    [InlineData(TerminalKey.Enter, TerminalModifiers.Alt, false, "\u001b\r")]
    [InlineData(TerminalKey.Tab, TerminalModifiers.None, false, "\t")]
    [InlineData(TerminalKey.Tab, TerminalModifiers.Shift, false, "\u001b[Z")]
    [InlineData(TerminalKey.Backspace, TerminalModifiers.None, false, "\u007f")]
    [InlineData(TerminalKey.Backspace, TerminalModifiers.Control, false, "\b")]
    [InlineData(TerminalKey.Escape, TerminalModifiers.None, false, "\u001b")]
    public void Special_keys_become_the_sequences_terminals_expect(TerminalKey key, TerminalModifiers modifiers, bool applicationCursorKeys, string expected) =>
        Assert.Equal(expected, Text(TerminalInput.EncodeKey(key, modifiers, applicationCursorKeys)));

    [Theory]
    [InlineData('c', TerminalModifiers.Control, "\u0003")]
    [InlineData('C', TerminalModifiers.Control, "\u0003")]
    [InlineData('a', TerminalModifiers.Control, "\u0001")]
    [InlineData('z', TerminalModifiers.Control, "\u001a")]
    [InlineData('[', TerminalModifiers.Control, "\u001b")]
    [InlineData(' ', TerminalModifiers.Control, "\0")]
    [InlineData('\\', TerminalModifiers.Control, "\u001c")]
    [InlineData('x', TerminalModifiers.Alt, "\u001bx")]
    [InlineData('d', TerminalModifiers.Alt | TerminalModifiers.Control, "\u001b\u0004")]
    public void Control_and_alt_combinations_become_control_codes_and_escape_prefixes(char character, TerminalModifiers modifiers, string expected) =>
        Assert.Equal(expected, Text(TerminalInput.EncodeControl(character, modifiers)));

    [Fact]
    public void Combinations_without_a_terminal_meaning_are_reported_as_such() =>
        Assert.Null(TerminalInput.EncodeControl('9', TerminalModifiers.Control));

    [Fact]
    public void Typed_text_is_sent_as_utf8()
    {
        Assert.Equal("héllo 你好 😀", Text(TerminalInput.EncodeText("héllo 你好 😀")));
        Assert.Equal([0xE4, 0xBD, 0xA0], TerminalInput.EncodeText("你"));
    }

    [Fact]
    public void Pastes_turn_line_breaks_into_enter_and_are_bracketed_on_request()
    {
        Assert.Equal("a\rb\rc", Text(TerminalInput.EncodePaste("a\r\nb\nc", bracketed: false)));
        Assert.Equal($"{Esc}[200~a\rb{Esc}[201~", Text(TerminalInput.EncodePaste("a\nb", bracketed: true)));
    }

    [Fact]
    public void A_paste_cannot_close_the_bracket_early()
    {
        var sent = Text(TerminalInput.EncodePaste($"safe{Esc}[201~rm -rf /", bracketed: true));

        Assert.Equal($"{Esc}[200~saferm -rf /{Esc}[201~", sent);
        Assert.Equal(1, sent.Split($"{Esc}[201~").Length - 1);
    }

    [Fact]
    public void Mouse_reports_use_the_sgr_or_the_legacy_encoding()
    {
        Assert.Equal($"{Esc}[<0;11;6M", Text(TerminalInput.EncodeMouse(MouseAction.Press, 0, 10, 5, TerminalModifiers.None, sgr: true)));
        Assert.Equal($"{Esc}[<0;11;6m", Text(TerminalInput.EncodeMouse(MouseAction.Release, 0, 10, 5, TerminalModifiers.None, sgr: true)));
        Assert.Equal($"{Esc}[<34;1;1M", Text(TerminalInput.EncodeMouse(MouseAction.Move, 2, 0, 0, TerminalModifiers.None, sgr: true)));
        Assert.Equal($"{Esc}[<64;3;4M", Text(TerminalInput.EncodeMouse(MouseAction.WheelUp, 0, 2, 3, TerminalModifiers.None, sgr: true)));
        Assert.Equal($"{Esc}[<20;1;1M", Text(TerminalInput.EncodeMouse(MouseAction.Press, 0, 0, 0, TerminalModifiers.Shift | TerminalModifiers.Control, sgr: true)));

        Assert.Equal([0x1B, (byte)'[', (byte)'M', 32, 33, 33], TerminalInput.EncodeMouse(MouseAction.Press, 0, 0, 0, TerminalModifiers.None, sgr: false));
        Assert.Equal([0x1B, (byte)'[', (byte)'M', 35, 33, 33], TerminalInput.EncodeMouse(MouseAction.Release, 0, 0, 0, TerminalModifiers.None, sgr: false));
        Assert.Null(TerminalInput.EncodeMouse(MouseAction.Press, 0, 300, 0, TerminalModifiers.None, sgr: false));
    }
}
