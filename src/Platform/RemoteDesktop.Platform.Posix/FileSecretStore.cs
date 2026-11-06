using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Platform;

namespace RemoteDesktop.Platform.Posix;

/// <summary>
/// Stores secrets as plain files in a directory only the owner may enter.
/// </summary>
/// <remarks>
/// <para>
/// The threat that matters here is another account on the same machine, and a 0700 directory owned by the user answers
/// it completely. It does not answer a stolen disk — but neither does DPAPI without full-disk encryption, and DPAPI's
/// machine scope is <em>weaker</em> than this: any process on the computer can unprotect those files, which is why the
/// Windows store has to restrict the folder as well.
/// </para>
/// <para>
/// libsecret (the freedesktop keyring) is the closer equivalent and is deliberately not used as the default. This store
/// is reached from a <c>systemd --user</c> service, and whether the login keyring is unlocked at that point depends on
/// whether the user logged in with a password, on autologin, and on the greeter's configuration. When it is locked,
/// libsecret either blocks on a prompt nobody can answer or fails outright. A secret store that is sometimes
/// unavailable is worse than one that is always available and marginally weaker.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class FileSecretStore(string directory) : ISecretStore
{
    // Only the owner may enter the folder or read the files inside it.
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public void Save(string name, ReadOnlySpan<byte> secret)
    {
        EnsureDirectory();
        var path = GetPath(name);

        // Written beside the file and moved into place, because a write that stops halfway leaves a file that can no
        // longer be read — and for the device identity that means a machine that silently comes back as a different
        // device, with a different code, that nobody can connect to any more.
        var temporary = path + ".tmp";

        // Any leftover from an interrupted save is removed so the file is always created fresh, with the owner-only
        // mode applied at open(2) time rather than after the secret is already on disk.
        File.Delete(temporary);
        using (var stream = new FileStream(temporary, new FileStreamOptions
        {
            Mode = System.IO.FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = FileMode,
        }))
        {
            stream.Write(secret);
        }

        File.SetUnixFileMode(temporary, FileMode);
        File.Move(temporary, path, overwrite: true);
    }

    public byte[]? Load(string name)
    {
        var path = GetPath(name);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public void Delete(string name) => File.Delete(GetPath(name));

    private void EnsureDirectory()
    {
        // CreateDirectory(path, mode) applies the mode to this folder only; the parents keep the umask's rules, which
        // is what the Windows store does with its ACL as well.
        Directory.CreateDirectory(directory, DirectoryMode);
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
