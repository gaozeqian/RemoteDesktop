using RemoteDesktop.Domain;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The rule for what may be typed into the server-address box.
/// </summary>
/// <remarks>
/// One rule for both platforms, which is why it is worth testing at this level rather than through either of them:
/// the value ends up in a settings file that a service reads and uses to open outbound connections, and the failure
/// that matters is the address Windows accepts and Linux refuses.
/// </remarks>
public sealed class RendezvousAddressTests
{
    [Theory]
    [InlineData("ws://127.0.0.1:5080/ws")]
    [InlineData("wss://rd.example.com/ws")]
    [InlineData("ws://rd.example.com")]
    [InlineData("  ws://rd.example.com/ws  ")] // typed with the spaces people type
    [InlineData("wss://[2001:db8::1]:5080/ws")] // an IPv6 literal keeps its brackets
    [InlineData("ws://192.168.1.10:5080/ws")]
    [InlineData("ws://my-host.local:5080/ws")]
    public void An_address_a_server_could_actually_be_reached_at_is_accepted(string address)
    {
        Assert.True(RendezvousAddress.TryParse(address, out var parsed), address);

        // The scheme survives parsing rather than being assumed: telling a ws address from a wss one is the
        // difference between a connection and no connection, and it is the one part of this that a caller reads.
        Assert.Equal(address.Trim().StartsWith("wss", StringComparison.Ordinal) ? "wss" : "ws", parsed.Value.Scheme);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("rd.example.com/ws")] // no scheme
    [InlineData("http://rd.example.com/ws")] // the wrong scheme: this is not a web page
    [InlineData("wss://")] // no host
    [InlineData("ws://exa mple.com/ws")]
    [InlineData("ws://exa\"mple.com/ws")]
    [InlineData("ws://exam;ple.com/ws")]
    [InlineData("ws://exa'mple.com/ws")]
    public void Anything_else_is_refused(string? address)
    {
        // The host is held to what a DNS name or an IP literal can contain. Anything else is somebody's fragment of
        // something else — this is typed by hand into a box, and the quotes and semicolons matter downstream.
        Assert.False(RendezvousAddress.TryParse(address, out _), address ?? "(null)");
    }

    [Fact]
    public void It_normalises_to_the_form_that_gets_written_down()
    {
        Assert.True(RendezvousAddress.TryParse("  ws://rd.example.com:5080/ws ", out var parsed));

        // What is stored is the parsed form, not what was typed: the service parses the file's contents in turn, and
        // the two must agree about an address that differs only in its whitespace.
        Assert.Equal("ws://rd.example.com:5080/ws", parsed.AbsoluteUri);
        Assert.Equal(parsed.AbsoluteUri, parsed.ToString());
    }

    [Fact]
    public void Parse_says_what_was_wrong_rather_than_returning_an_empty_one()
    {
        var refused = Assert.Throws<FormatException>(() => RendezvousAddress.Parse("http://rd.example.com/ws"));
        Assert.Contains("http://rd.example.com/ws", refused.Message, StringComparison.Ordinal);
    }
}
