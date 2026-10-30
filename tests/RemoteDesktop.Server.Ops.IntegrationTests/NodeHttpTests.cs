using RemoteDesktop.Server.Ops;
using RemoteDesktop.Server.Ops.Core;

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

    [Fact]
    public void A_listed_host_may_receive_the_token_and_an_unlisted_one_may_not()
    {
        string[] allowed = ["node.example.com"];

        Assert.True(NodeHttp.TokenCanTravelTo("https://node.example.com:5080", "secret", false, allowed));
        Assert.False(NodeHttp.TokenCanTravelTo("https://elsewhere.example.com:5080", "secret", false, allowed));
    }

    /// <summary>The installers seed the local nodes on loopback, so a list of remote servers must not cut them off.</summary>
    [Fact]
    public void Loopback_is_allowed_even_when_the_list_names_only_remote_hosts()
    {
        Assert.True(NodeHttp.TokenCanTravelTo("https://127.0.0.1:5080", "secret", false, ["node.example.com"]));
    }

    [Fact]
    public void Without_a_token_there_is_nothing_to_guard()
    {
        Assert.True(NodeHttp.TokenCanTravelTo("https://elsewhere.example.com:5080", string.Empty, false, ["node.example.com"]));
    }

    [Fact]
    public void The_refusal_names_the_setting_that_would_allow_the_address()
    {
        var insecure = NodeHttp.RefusalReason("http://node.example.com:5080", Options(token: "secret"));
        var unlisted = NodeHttp.RefusalReason("https://elsewhere.example.com:5080", Options(token: "secret", hosts: ["node.example.com"]));

        Assert.NotNull(insecure);
        Assert.NotNull(unlisted);
        Assert.Contains("AllowInsecureNodeHttp", insecure, StringComparison.Ordinal);
        Assert.Contains("NodeTokenHosts", unlisted, StringComparison.Ordinal);
        Assert.Null(NodeHttp.RefusalReason("https://node.example.com:5080", Options(token: "secret", hosts: ["node.example.com"])));
        Assert.Null(NodeHttp.RefusalReason("http://node.example.com:5080", Options(token: string.Empty)));
    }

    /// <summary>A list entry with a scheme or a port would silently never match the host of a node URL.</summary>
    [Theory]
    [InlineData("https://node.example.com")]
    [InlineData("node.example.com:5080")]
    [InlineData("node.example.com/api")]
    [InlineData(" ")]
    public void A_host_list_entry_that_is_not_a_bare_host_is_refused_at_startup(string entry)
    {
        var options = Options(token: "secret", hosts: [entry]);

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void A_bare_host_entry_is_accepted_and_trimmed()
    {
        var options = Options(token: "secret", hosts: [" node.example.com "]);

        options.Validate();

        Assert.Equal(["node.example.com"], options.NodeTokenHosts);
    }

    private static OpsOptions Options(string token, List<string>? hosts = null) => new()
    {
        Database = new OpsDatabaseSettings { Path = "ops.db" },
        NodeToken = token,
        NodeTokenHosts = hosts ?? [],
    };
}
