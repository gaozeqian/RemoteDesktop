using System.Net;

namespace RemoteDesktop.Server.Shared;

/// <summary>
/// The reverse proxies whose <c>X-Forwarded-*</c> headers a server believes, parsed from its <c>TrustedProxies</c>
/// setting. Kept free of ASP.NET types so that this project does not take on the web framework: each host copies the
/// result into its own <c>ForwardedHeadersOptions</c>.
/// </summary>
public sealed class TrustedProxyList
{
    private TrustedProxyList(IReadOnlyList<IPAddress> addresses, IReadOnlyList<IPNetwork> networks)
    {
        Addresses = addresses;
        Networks = networks;
    }

    /// <summary>Entries that were single addresses.</summary>
    public IReadOnlyList<IPAddress> Addresses { get; }

    /// <summary>Entries that were CIDR networks.</summary>
    public IReadOnlyList<IPNetwork> Networks { get; }

    /// <summary>
    /// Parses the entries of a <c>TrustedProxies</c> setting; each is an IP address or a CIDR network.
    /// </summary>
    /// <returns>
    /// The proxies, or <see langword="null"/> when there are none. The forwarded-headers middleware only checks who is
    /// speaking when at least one of its two lists is non-empty; with both empty it applies the headers of any caller.
    /// "No trusted proxy" therefore has to mean "no middleware", and a caller that gets <see langword="null"/> must not
    /// add it.
    /// </returns>
    /// <remarks>
    /// An entry that is neither an address nor a network is skipped: the settings validate their entries at start-up,
    /// so none reaches this point.
    /// </remarks>
    public static TrustedProxyList? Parse(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var addresses = new List<IPAddress>();
        var networks = new List<IPNetwork>();
        foreach (var entry in entries)
        {
            if (IPAddress.TryParse(entry, out var address))
            {
                addresses.Add(address);
            }
            else if (IPNetwork.TryParse(entry, out var network))
            {
                networks.Add(network);
            }
        }

        return addresses.Count == 0 && networks.Count == 0 ? null : new TrustedProxyList(addresses, networks);
    }
}
