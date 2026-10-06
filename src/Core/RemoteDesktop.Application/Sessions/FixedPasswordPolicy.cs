using System.Text;
using RemoteDesktop.Abstractions.Platform;

namespace RemoteDesktop.Application.Sessions;

/// <summary>
/// Persistence of the optional fixed password used for unattended access. Encrypted with DPAPI on Windows through
/// <see cref="ISecretStore"/>, surviving restarts.
/// </summary>
public static class FixedPasswordPolicy
{
    private const string FixedPasswordSecret = "credentials.fixed-password";

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
    public static void Save(ISecretStore store, string? password)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrWhiteSpace(password))
        {
            store.Delete(FixedPasswordSecret);
        }
        else
        {
            store.Save(FixedPasswordSecret, Encoding.UTF8.GetBytes(password.Trim()));
        }
    }
}
