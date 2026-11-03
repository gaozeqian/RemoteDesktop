using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace RemoteDesktop.Server.Shared;

/// <summary>
/// The bearer token the console presents to a node's <c>/metrics</c> and <c>/internal/*</c> endpoints.
/// </summary>
/// <remarks>
/// By default one token serves the whole deployment (<c>Ops:NodeToken</c> on the console, the same value as
/// <c>NodeToken</c> on the node). The console can also hold a token of its own for a single node and present that one
/// instead; the node still checks whatever it was configured with, so the node's setting and the console's token for
/// it must match. Either way the console has to send the secret back, so it can only be stored in clear — in
/// configuration for the deployment-wide token, in the console's database for a per-node one. Rotation is a
/// configuration change on the node and on the console, documented in <c>docs/ops/README.md</c>.
/// <para>
/// When no token is configured the endpoints are not open to everyone: they are open only to callers on the loopback
/// interface. The signaling server listens on <c>0.0.0.0</c> by default, so "no token" used to mean "anyone who can
/// reach the port", which exposed device enumeration and device revocation on a public server. A console on another
/// machine, or in another container, needs the token configured on the node it reads and on the console itself.
/// </para>
/// </remarks>
public static class NodeTokenGuard
{
    private const string Scheme = "Bearer ";

    /// <summary>
    /// True when <paramref name="authorization"/> carries <paramref name="expected"/>, or when no token is configured
    /// and <paramref name="remoteAddress"/> is the loopback interface.
    /// </summary>
    /// <remarks>
    /// The address is the one the request ends up with after the host's forwarded-headers processing. In the
    /// signaling server that is the socket peer for an untrusted caller, or the client address supplied by a proxy
    /// listed in <c>Rendezvous:TrustedProxies</c>. An untrusted caller cannot forge it; a trusted proxy that forwards
    /// <c>X-Forwarded-For: 127.0.0.1</c> can, which is why only proxies the deployment names are trusted. The relay
    /// does not enable forwarded headers, so there it is always the socket peer.
    /// </remarks>
    public static bool IsAuthorized(string? authorization, string? expected, IPAddress? remoteAddress)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return IsLoopback(remoteAddress);
        }

        if (string.IsNullOrEmpty(authorization) || !authorization.StartsWith(Scheme, StringComparison.Ordinal))
        {
            return false;
        }

        var presented = authorization[Scheme.Length..].Trim();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>
    /// Loopback, including the IPv4-mapped IPv6 form a dual-stack listener may report for a 127.0.0.1 connection.
    /// </summary>
    private static bool IsLoopback(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        return IPAddress.IsLoopback(address)
            || (address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4()));
    }
}
