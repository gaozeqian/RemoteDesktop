using System.Net;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The token check in front of a node's <c>/metrics</c> and <c>/internal/*</c> endpoints.
/// </summary>
/// <remarks>
/// The "no token" case is the one that matters: the signaling server listens on 0.0.0.0 by default, so treating an
/// absent token as "open" exposed device enumeration and device revocation to every caller that could reach the
/// port. Without a token only a caller on this machine may use the internal face.
/// </remarks>
public sealed class NodeTokenGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void Without_a_token_only_loopback_is_authorized(string address)
    {
        Assert.True(NodeTokenGuard.IsAuthorized(null, null, IPAddress.Parse(address)));
        Assert.True(NodeTokenGuard.IsAuthorized("Bearer anything", null, IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("203.0.113.10")]
    [InlineData("::ffff:10.0.0.1")]
    public void Without_a_token_a_remote_address_is_refused(string address)
    {
        Assert.False(NodeTokenGuard.IsAuthorized(null, null, IPAddress.Parse(address)));
        Assert.False(NodeTokenGuard.IsAuthorized("Bearer anything", null, IPAddress.Parse(address)));
    }

    [Fact]
    public void Without_a_token_an_unknown_address_is_refused()
    {
        Assert.False(NodeTokenGuard.IsAuthorized(null, null, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic secret")]
    [InlineData("bearer secret")]
    [InlineData("Bearer wrong")]
    [InlineData("Bearer secret-extra")]
    public void A_configured_token_must_match_exactly(string? authorization)
    {
        Assert.False(NodeTokenGuard.IsAuthorized(authorization, "secret", IPAddress.Loopback));
        Assert.False(NodeTokenGuard.IsAuthorized(authorization, "secret", IPAddress.Parse("203.0.113.10")));
    }

    [Fact]
    public void A_configured_token_is_required_even_on_loopback()
    {
        Assert.False(NodeTokenGuard.IsAuthorized(null, "secret", IPAddress.Loopback));
        Assert.True(NodeTokenGuard.IsAuthorized("Bearer secret", "secret", IPAddress.Loopback));
    }

    [Fact]
    public void A_configured_token_travels_over_the_network()
    {
        Assert.True(NodeTokenGuard.IsAuthorized("Bearer secret", "secret", IPAddress.Parse("203.0.113.10")));
    }
}
