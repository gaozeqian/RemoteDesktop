using System.Text;
using RemoteDesktop.Abstractions.Platform;

namespace RemoteDesktop.Application.Sessions;

/// <summary>
/// Persistence of the optional fixed password used for unattended access. Encrypted with DPAPI on Windows through
/// <see cref="ISecretStore"/>, surviving restarts.
/// </summary>
public static class FixedPasswordPolicy
{
    /// <summary>How short a fixed password may be. The window that asks for one shows the same number.</summary>
    public const int MinimumLength = 6;

    private const string FixedPasswordSecret = "credentials.fixed-password";

    /// <summary>
    /// Whether this password may be stored. The rule is enforced here rather than only in the window that collects
    /// it: the service accepts the setting over a pipe, so anything that can reach the service could otherwise leave
    /// a one-character password on a machine that is meant to be reachable without anybody present.
    /// </summary>
    public static bool IsAcceptable(string? password) =>
        password is not null && password.Trim().Length >= MinimumLength;

    /// <summary>Loads the stored fixed password, or <c>null</c> if not set or cannot be read.</summary>
    public static string? Load(ISecretStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            var bytes = store.Load(FixedPasswordSecret);
            if (bytes is null or { Length: 0 })
            {
                return null;
            }

            var password = Encoding.UTF8.GetString(bytes).Trim();
            return string.IsNullOrEmpty(password) ? null : password;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Saves or clears the fixed password in the store.</summary>
    /// <exception cref="ArgumentException">The password is too short to be one.</exception>
    public static void Save(ISecretStore store, string? password)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrWhiteSpace(password))
        {
            store.Delete(FixedPasswordSecret);
            return;
        }

        if (!IsAcceptable(password))
        {
            throw new ArgumentException($"A fixed password must be at least {MinimumLength} characters.", nameof(password));
        }

        store.Save(FixedPasswordSecret, Encoding.UTF8.GetBytes(password.Trim()));
    }
}
