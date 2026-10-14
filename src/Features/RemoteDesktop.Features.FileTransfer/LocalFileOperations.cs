using RemoteDesktop.Protocol.File;

namespace RemoteDesktop.Features.FileTransfer;

/// <summary>
/// Browsing and folder operations on this computer's files, through a <see cref="FileAccessPolicy"/>. Used by the host
/// to answer the controller and by the local pane of the file manager. Failures are reported as
/// <see cref="FileOperationException"/> with a reason the other side can show.
/// </summary>
public sealed class LocalFileOperations(FileAccessPolicy policy)
{
    public const int MaxEntries = 20_000;

    /// <summary>Lists a folder; an empty path lists the drives.</summary>
    public DirectoryContents List(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return new DirectoryContents(string.Empty, string.Empty, ListDrives(), Truncated: false);
        }

        var full = policy.Normalize(path);
        return Guard(full, () =>
        {
            if (!Directory.Exists(full))
            {
                throw File.Exists(full)
                    ? new FileOperationException(FileError.InvalidPath, "That is a file, not a folder.")
                    : new FileOperationException(FileError.NotFound, "The folder does not exist.");
            }

            var entries = new List<FileEntryInfo>();
            var truncated = false;
            foreach (var info in new DirectoryInfo(full).EnumerateFileSystemInfos())
            {
                if (entries.Count >= MaxEntries)
                {
                    truncated = true;
                    break;
                }

                entries.Add(ToEntry(info));
            }

            entries.Sort((a, b) => a.IsDirectory != b.IsDirectory
                ? b.IsDirectory.CompareTo(a.IsDirectory)
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            var parent = Path.GetDirectoryName(full) ?? string.Empty;
            return new DirectoryContents(full, parent, entries, truncated);
        });
    }

    /// <summary>Drives and the usual folders of the signed-in user, as starting points.</summary>
    public IReadOnlyList<PlaceInfo> GetPlaces()
    {
        var places = new List<PlaceInfo>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = OperatingSystem.IsWindows()
            ? new[]
            {
                home,
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                string.IsNullOrEmpty(home) ? string.Empty : Path.Combine(home, "Downloads"),
            }
            :
            [
                home,
                // From the desktop's own file, because SpecialFolder answers with English names under the home
                // directory and a desktop that is not in English does not use them — on a Chinese install the desktop
                // is ~/桌面, and asking for "Desktop" there finds nothing at all.
                XdgUserDirectories.Desktop ?? string.Empty,
                XdgUserDirectories.Documents ?? string.Empty,
                XdgUserDirectories.Downloads ?? string.Empty,
            ];

        foreach (var candidate in candidates.Where(c => !string.IsNullOrEmpty(c) && Directory.Exists(c)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                places.Add(new PlaceInfo(Path.GetFileName(candidate), policy.Normalize(candidate), IsDrive: false));
            }
            catch (FileOperationException)
            {
                // Outside the shared roots: not offered.
            }
        }

        foreach (var drive in ListDrives())
        {
            try
            {
                places.Add(new PlaceInfo(drive.Name, policy.Normalize(drive.Name), IsDrive: true));
            }
            catch (FileOperationException)
            {
            }
        }

        return places;
    }

    public void MakeDirectory(string path)
    {
        var full = policy.Normalize(path);
        Guard(full, () =>
        {
            if (Directory.Exists(full) || File.Exists(full))
            {
                throw new FileOperationException(FileError.AlreadyExists, "Something with that name already exists.");
            }

            Directory.CreateDirectory(full);
            return 0;
        });
    }

    public void Rename(string path, string newName)
    {
        var full = policy.Normalize(path);
        var name = FileAccessPolicy.NormalizeName(newName);
        if (policy.IsProtectedRoot(full))
        {
            throw new FileOperationException(FileError.NotAllowed, "That location cannot be renamed.");
        }

        var target = policy.Normalize(Path.Combine(Path.GetDirectoryName(full) ?? string.Empty, name));
        Guard(full, () =>
        {
            if (Directory.Exists(target) || File.Exists(target))
            {
                throw new FileOperationException(FileError.AlreadyExists, "Something with that name already exists.");
            }

            if (Directory.Exists(full))
            {
                Directory.Move(full, target);
            }
            else if (File.Exists(full))
            {
                File.Move(full, target);
            }
            else
            {
                throw new FileOperationException(FileError.NotFound, "The item does not exist.");
            }

            return 0;
        });
    }

    public void Delete(string path, bool recursive)
    {
        var full = policy.Normalize(path);
        if (policy.IsProtectedRoot(full))
        {
            throw new FileOperationException(FileError.NotAllowed, "That location cannot be deleted.");
        }

        Guard(full, () =>
        {
            if (Directory.Exists(full))
            {
                // Links are removed, never followed into their target.
                if (!recursive && Directory.EnumerateFileSystemEntries(full).Any())
                {
                    throw new FileOperationException(FileError.Io, "The folder is not empty.");
                }

                Directory.Delete(full, recursive);
            }
            else if (File.Exists(full))
            {
                File.Delete(full);
            }
            else
            {
                throw new FileOperationException(FileError.NotFound, "The item does not exist.");
            }

            return 0;
        });
    }

    /// <summary>Filesystem types that are not somewhere a person keeps files.</summary>
    /// <remarks>
    /// Every one of these appears in <see cref="DriveInfo.GetDrives"/> on a Linux machine and none of them is a disk:
    /// squashfs is a snap, overlay is a container's own filesystem, and the rest are the kernel's. Offering them
    /// would fill the drive list with entries nobody can put a file on.
    /// </remarks>
    private static readonly string[] NotADriveFormats =
        ["squashfs", "overlay", "tmpfs", "devtmpfs", "ramfs", "sysfs", "proc", "cgroup", "cgroup2", "autofs", "nsfs", "efivarfs", "debugfs", "tracefs"];

    /// <summary>Filesystem types whose size cannot be asked for without a round trip to another machine.</summary>
    private static readonly string[] NetworkFormats = ["nfs", "nfs4", "cifs", "smb", "smb2", "smb3", "fuseblk", "fuse.sshfs"];

    private static List<FileEntryInfo> ListDrives()
    {
        var drives = new List<FileEntryInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                // The format is the filesystem type on Unix and something like "NTFS" on Windows, where none of the
                // names below are possible. Filtering there would be pointless; filtering here is what stops a snap
                // from being offered as a disk.
                var format = drive.DriveFormat;
                if (Array.Exists(NotADriveFormats, name => string.Equals(name, format, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                // Asking a network mount how big it is means asking the server, and a server that has gone away makes
                // that wait — on the thread that is drawing the drive list. The size is not what the list is for.
                var size = Array.Exists(NetworkFormats, name => string.Equals(name, format, StringComparison.OrdinalIgnoreCase))
                    ? 0
                    : drive.TotalSize;

                drives.Add(new FileEntryInfo(drive.Name, IsDirectory: true, size, DateTimeOffset.MinValue, FileEntryAttributes.None));
            }
            catch (IOException)
            {
                // A drive that went away while being asked.
            }
        }

        return drives;
    }

    private static FileEntryInfo ToEntry(FileSystemInfo info)
    {
        var attributes = FileEntryAttributes.None;
        try
        {
            attributes = (info.Attributes.HasFlag(FileAttributes.Hidden) ? FileEntryAttributes.Hidden : 0) |
                         (info.Attributes.HasFlag(FileAttributes.ReadOnly) ? FileEntryAttributes.ReadOnly : 0) |
                         (info.Attributes.HasFlag(FileAttributes.System) ? FileEntryAttributes.System : 0);
        }
        catch (IOException)
        {
            // Attributes of a vanishing entry; listed without them.
        }

        var isDirectory = info is DirectoryInfo;
        long size = 0;
        var modified = DateTimeOffset.MinValue;
        try
        {
            size = info is FileInfo file ? file.Length : 0;
            modified = info.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return new FileEntryInfo(info.Name, isDirectory, size, modified, attributes);
    }

    // Maps the exceptions of the file system to the reasons the protocol knows.
    private static T Guard<T>(string path, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (FileOperationException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new FileOperationException(FileError.AccessDenied, "Access to that location was denied.", ex);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new FileOperationException(FileError.NotFound, "The item does not exist.", ex);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            throw new FileOperationException(FileError.Io, $"The operation on '{Path.GetFileName(path)}' failed: {ex.Message}", ex);
        }
    }
}
