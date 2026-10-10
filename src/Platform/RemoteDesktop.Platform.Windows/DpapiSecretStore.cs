using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
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
        EnsureDirectory();
        var protectedBytes = ProtectedData.Protect(secret.ToArray(), Entropy, scope);
        var path = GetPath(name);

        // Written beside the file and moved into place, because a write that stops halfway leaves a file that can no
        // longer be unprotected — and for the device identity that means a machine that silently comes back as a
        // different device, with a different code, that nobody can connect to any more.
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, protectedBytes);
        File.Move(temporary, path, overwrite: true);
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

    // Machine-scope DPAPI lets any process on the computer decrypt the files, so the folder itself is what protects the
    // device key: only SYSTEM, administrators and the account running this process get in.
    private void EnsureDirectory()
    {
        if (Directory.Exists(directory) || scope != DataProtectionScope.LocalMachine)
        {
            Directory.CreateDirectory(directory);
            return;
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), WindowsIdentity.GetCurrent().User! })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        // CreateDirectory(security) would give the missing parent folders the same restrictive rules; only this folder needs them.
        if (Path.GetDirectoryName(directory) is { Length: > 0 } parent)
        {
            Directory.CreateDirectory(parent);
        }

        security.CreateDirectory(directory);
    }

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
