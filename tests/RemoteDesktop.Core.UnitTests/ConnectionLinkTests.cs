using RemoteDesktop.Application.Links;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// "remotedesktop://" links arrive from browsers, chat messages and e-mail, so the parser is the boundary: it takes
/// the whole URL apart and yields a device code, or nothing at all. A link never carries a password — there is nowhere
/// for one to go.
/// </summary>
public sealed class ConnectionLinkTests
{
    [Theory]
    [InlineData("remotedesktop://connect?code=902226105")]
    [InlineData("remotedesktop://connect?code=902%20226%20105")]
    [InlineData("remotedesktop://connect/?code=902226105")]
    [InlineData("remotedesktop://connect?user=someone&code=902226105")]
    [InlineData("remotedesktop://902226105")]
    [InlineData("remotedesktop://902 226 105")]
    [InlineData("remotedesktop://902-226-105")]
    [InlineData("remotedesktop:/902226105")]
    [InlineData("REMOTEDESKTOP://CONNECT?CODE=902226105")]
    [InlineData("  remotedesktop://connect?code=902226105  ")]
    public void A_link_carries_the_device_code_in_any_of_the_shapes_people_write(string text)
    {
        Assert.True(ConnectionLink.TryParse(text, out var link));
        Assert.Equal("902226105", link.Device.ToString());
    }

    /// <summary>A query is not a place for credentials, and the type has no way to hold one.</summary>
    [Fact]
    public void A_password_in_the_link_is_not_taken_from_it()
    {
        Assert.True(ConnectionLink.TryParse("remotedesktop://connect?code=902226105&password=hunter2", out var link));

        Assert.Equal("902226105", link.Device.ToString());
        Assert.DoesNotContain("password", link.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2", link.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/connect?code=902226105")]
    [InlineData("remotedesktop")]
    [InlineData("remotedesktop:")]
    [InlineData("remotedesktop://")]
    [InlineData("remotedesktop://connect")]
    [InlineData("remotedesktop://connect?code=")]
    [InlineData("remotedesktop://connect?code=12345678")]
    [InlineData("remotedesktop://connect?code=002226105")]
    [InlineData("remotedesktop://connect?code=9022261056")]
    [InlineData("remotedesktop://902226105?code=902226105")]
    [InlineData("remotedesktop://something")]
    public void Anything_else_is_not_a_link(string? text)
    {
        Assert.False(ConnectionLink.TryParse(text, out _));
    }

    /// <summary>A link from somewhere else must not become an unbounded parse.</summary>
    [Fact]
    public void An_absurdly_long_url_is_refused()
    {
        var text = "remotedesktop://connect?code=" + new string('9', 4096);

        Assert.False(ConnectionLink.TryParse(text, out _));
    }
}
