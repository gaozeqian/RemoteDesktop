using System.Net;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops.UnitTests;

/// <summary>
/// "No trusted proxy" has to come back as <see langword="null"/>: the forwarded-headers middleware believes every
/// caller when both of its lists are empty, so a server that is given nothing to trust must not add it at all.
/// </summary>
public class TrustedProxyListTests
{
    [Fact]
    public void No_entries_means_no_list_so_that_no_middleware_is_added() =>
        Assert.Null(TrustedProxyList.Parse([]));

    [Fact]
    public void Entries_that_are_neither_an_address_nor_a_network_leave_nothing_to_trust() =>
        Assert.Null(TrustedProxyList.Parse(["", "not-an-address", "10.0.0.0/99"]));

    [Fact]
    public void An_address_is_a_known_proxy()
    {
        var list = TrustedProxyList.Parse(["10.1.2.3", "::1"]);

        Assert.NotNull(list);
        Assert.Equal([IPAddress.Parse("10.1.2.3"), IPAddress.IPv6Loopback], list.Addresses);
        Assert.Empty(list.Networks);
    }

    [Fact]
    public void A_cidr_range_is_a_known_network()
    {
        var list = TrustedProxyList.Parse(["10.0.0.0/8", "fd00::/8"]);

        Assert.NotNull(list);
        Assert.Empty(list.Addresses);
        Assert.Equal([IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("fd00::/8")], list.Networks);
    }

    [Fact]
    public void A_mixture_is_split_in_order()
    {
        var list = TrustedProxyList.Parse(["192.168.0.0/16", "172.16.0.5", "10.0.0.0/8", "172.16.0.6"]);

        Assert.NotNull(list);
        Assert.Equal([IPAddress.Parse("172.16.0.5"), IPAddress.Parse("172.16.0.6")], list.Addresses);
        Assert.Equal([IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("10.0.0.0/8")], list.Networks);
    }

    [Fact]
    public void An_invalid_entry_is_skipped_and_the_valid_ones_kept()
    {
        var list = TrustedProxyList.Parse(["bogus", "10.1.2.3"]);

        Assert.NotNull(list);
        Assert.Equal([IPAddress.Parse("10.1.2.3")], list.Addresses);
        Assert.Empty(list.Networks);
    }
}
