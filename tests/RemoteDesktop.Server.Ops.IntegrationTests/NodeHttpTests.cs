using RemoteDesktop.Server.Ops;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

public class NodeHttpTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5080", false)]
    [InlineData("http://127.0.0.1:5080", true)]
    [InlineData("https://node.example.com:5080", false)]
    [InlineData("http://node.example.com:5080", true)]
    public void The_token_may_travel_when_the_address_is_https_loopback_or_explicitly_opted_in(string baseUrl, bool allowInsecureHttp)
    {
        Assert.True(NodeHttp.TokenCanTravelTo(baseUrl, "secret", allowInsecureHttp));
    }

    [Fact]
    public void A_non_loopback_http_address_refuses_the_token_by_default()
    {
        Assert.False(NodeHttp.TokenCanTravelTo("http://node.example.com:5080", "secret"));
    }

    [Fact]
    public void An_empty_token_needs_no_transport_guard()
    {
        Assert.True(NodeHttp.TokenCanTravelTo("http://node.example.com:5080", string.Empty));
    }
}
