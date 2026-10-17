using System.Security.Cryptography;
using System.Text;

namespace RemoteDesktop.Server.Ops.Core;

/// <summary>
/// Session and CSRF tokens. The wire token is random and never stored; the database holds only its SHA-256, so a copy
/// of the ops database does not let anyone log in.
/// </summary>
public static class SessionToken
{
    private const int TokenBytes = 32;

    /// <summary>A new token, Base64Url so it survives a cookie without escaping.</summary>
    public static string Create() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes))
        .Replace('+', '-')
        .Replace('/', '_')
        .TrimEnd('=');

    /// <summary>The value stored for <paramref name="token"/>.</summary>
    public static string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
