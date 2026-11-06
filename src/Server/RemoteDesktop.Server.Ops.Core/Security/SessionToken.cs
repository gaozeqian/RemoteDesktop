using System.Security.Cryptography;
using System.Text;

namespace RemoteDesktop.Server.Ops.Core;

/// <summary>
/// Session and CSRF tokens. The wire token is random and never stored; the database holds only its SHA-256, so a copy
/// of the ops database does not let anyone log in. The CSRF token is not stored either: it is derived from the session
/// token, so it is bound to exactly one session and needs no server-side secret of its own.
/// </summary>
public static class SessionToken
{
    private const int TokenBytes = 32;

    private static readonly byte[] CsrfLabel = Encoding.UTF8.GetBytes("ops-csrf-v1");

    /// <summary>A new token, Base64Url so it survives a cookie without escaping.</summary>
    public static string Create() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes))
        .Replace('+', '-')
        .Replace('/', '_')
        .TrimEnd('=');

    /// <summary>
    /// The CSRF token that belongs to the session whose wire token is <paramref name="sessionToken"/>:
    /// HMAC-SHA256 keyed by the session token over a fixed label, Base64Url-encoded.
    /// </summary>
    /// <remarks>
    /// The session token is a 256-bit random secret that only the server and the HttpOnly cookie hold, which makes it a
    /// sound HMAC key by itself. A party that can merely write cookies (a sibling subdomain, say) cannot read it and so
    /// cannot compute the matching token; a different session, a signed-out session or a re-issued session has a
    /// different token and therefore a different CSRF value. The database holds only the session token's hash, so a
    /// copy of it does not yield CSRF tokens either.
    /// </remarks>
    public static string CsrfFor(string sessionToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionToken);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(sessionToken), CsrfLabel);
        return Convert.ToBase64String(mac).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>The value stored for <paramref name="token"/>.</summary>
    public static string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
