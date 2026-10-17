using System.Security.Cryptography;
using System.Text;

namespace RemoteDesktop.Server.Shared;

/// <summary>
/// The bearer token the console presents to a node's <c>/metrics</c> and <c>/internal/*</c> endpoints.
/// </summary>
/// <remarks>
/// One token for the deployment rather than one per node: the console has to send the same secret back, so a per-node
/// token only works if the console can recover it — which means storing it in clear, the same as this. Rotation is a
/// configuration change on all three processes, documented in <c>docs/ops/README.md</c>. When no token is configured
/// the endpoints are open, which is only acceptable because they are bound to loopback.
/// </remarks>
public static class NodeTokenGuard
{
    private const string Scheme = "Bearer ";

    /// <summary>True when <paramref name="authorization"/> carries <paramref name="expected"/>, or when none is required.</summary>
    public static bool IsAuthorized(string? authorization, string? expected)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return true;
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
}
