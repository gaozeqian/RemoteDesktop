using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops.UnitTests;

public class Base64KeyTests
{
    private static string Base64Of(int bytes) => Convert.ToBase64String(new byte[bytes]);

    [Fact]
    public void A_key_of_the_minimum_length_is_decoded()
    {
        var key = Base64Key.Decode(Base64Of(32), "bad", _ => "short");

        Assert.Equal(32, key.Length);
    }

    [Fact]
    public void Text_that_is_not_base64_gets_the_callers_message_and_no_inner_exception()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Base64Key.Decode("not base64 at all", "bad", _ => "short"));

        Assert.Equal("bad", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void A_short_key_gets_the_callers_message_built_from_its_length()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Base64Key.Decode(Base64Of(31), "bad", length => $"short:{length}"));

        Assert.Equal("short:31", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_text_decodes_to_nothing_and_is_short(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Base64Key.Decode(value, "bad", length => $"short:{length}"));

        Assert.Equal("short:0", error.Message);
    }

    [Fact]
    public void A_null_is_not_turned_into_a_refusal() =>
        Assert.Throws<ArgumentNullException>(() => Base64Key.Decode(null!, "bad", _ => "short"));

    [Fact]
    public void The_minimum_can_be_stated()
    {
        Assert.Equal(16, Base64Key.Decode(Base64Of(16), "bad", _ => "short", minimumBytes: 16).Length);
        Assert.Throws<InvalidOperationException>(() => Base64Key.Decode(Base64Of(15), "bad", _ => "short", minimumBytes: 16));
    }
}
