using System.Runtime.Versioning;
using System.Security.Cryptography;
using RemoteDesktop.Abstractions.Platform;

namespace RemoteDesktop.Platform.Windows;

/// <summary>
/// Stores secrets as DPAPI-protected files. <see cref="DataProtectionScope.LocalMachine"/> is used by the host
/// service (secrets must survive user switches); <see cref="DataProtectionScope.CurrentUser"/> by the client UI.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(string directory, DataProtectionScope scope) : ISecretStore
{
    private static readonly byte[] Entropy = "RemoteDesktop.SecretStore.v1"u8.ToArray();

    public void Save(string name, ReadOnlySpan<byte> secret)
    {
        Directory.CreateDirectory(directory);
        var protectedBytes = ProtectedData.Protect(secret.ToArray(), Entropy, scope);
        File.WriteAllBytes(GetPath(name), protectedBytes);
    }

    public byte[]? Load(string name)
    {
        var path = GetPath(name);
        if (!File.Exists(path))
        {
            return null;
        }

        return ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, scope);
    }

    public void Delete(string name) => File.Delete(GetPath(name));

    private string GetPath(string name)
    {
        // Names are internal constants, but reject anything that could escape the directory.
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid secret name '{name}'.", nameof(name));
        }

        return Path.Combine(directory, name + ".bin");
    }
}
