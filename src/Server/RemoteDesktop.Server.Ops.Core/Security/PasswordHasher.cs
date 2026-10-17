using System.Globalization;
using System.Security.Cryptography;

namespace RemoteDesktop.Server.Ops.Core;

/// <summary>
/// Password storage: PBKDF2-HMAC-SHA256 with a per-password salt, encoded as <c>pbkdf2-sha256$iterations$salt$hash</c>.
/// </summary>
/// <remarks>
/// The iteration count travels with the hash so an existing password keeps verifying after the default is raised — the
/// alternative is a migration nobody schedules. Parameters read back from storage are bounded before use: an encoded
/// value is written by an administrator, but a database is exactly the thing an attacker with write access already
/// owns, and an unbounded iteration count is a denial of service aimed at the login endpoint.
/// </remarks>
public static class PasswordHasher
{
    private const string Prefix = "pbkdf2-sha256";
    private const int DefaultIterations = 600_000;
    private const int MinimumIterations = 100_000;
    private const int MaximumIterations = 2_000_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    /// <summary>Hash of <paramref name="password"/>, safe to store as is.</summary>
    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, DefaultIterations, HashAlgorithmName.SHA256, HashSize);
        return string.Join(
            '$',
            Prefix,
            DefaultIterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    /// <summary>True when <paramref name="password"/> is the one <paramref name="encoded"/> was made from.</summary>
    public static bool Verify(string password, string encoded)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        var parts = encoded.Split('$');
        if (parts.Length != 4 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            || iterations is < MinimumIterations or > MaximumIterations)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length != SaltSize || expected.Length != HashSize)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
