namespace RemoteDesktop.Abstractions.Configuration;

/// <summary>
/// Parses the one address shape this product supports for its signaling server: <c>ws://host[:port][/path]</c>, or the
/// same wrapped in TLS as <c>wss://</c>.
/// </summary>
/// <remarks>
/// Shared rather than owned by one platform, because every end of the product has to agree on what an address is: the
/// Windows installer hands one to the service, the client validates whatever somebody typed into the box, and macOS
/// has no installer at all. One implementation is what stops an address the client accepts from being one the host
/// refuses — the two drifting apart is a bug that only shows up on a machine that cannot connect.
/// <para>
/// The host is held to what a DNS name or an IP literal can contain. That is stricter than <see cref="Uri"/> needs to
/// be, and deliberately so: the value is typed by hand, and a "host" carrying quotes or semicolons is somebody's
/// fragment of something else.
/// </para>
/// </remarks>
public static class RendezvousAddress
{
    /// <summary>True when <paramref name="address"/> is a usable signaling address; <paramref name="parsed"/> is the
    /// normalised form to store.</summary>
    public static bool TryParse(string? address, out Uri parsed)
    {
        parsed = null!;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var candidate)
            || candidate.Scheme is not ("ws" or "wss")
            || candidate.Host.Length == 0)
        {
            return false;
        }

        // The brackets around an IPv6 literal are not part of the address grammar this check is about.
        if (candidate.HostNameType != UriHostNameType.IPv6
            && !candidate.Host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
        {
            return false;
        }

        parsed = candidate;
        return true;
    }
}
