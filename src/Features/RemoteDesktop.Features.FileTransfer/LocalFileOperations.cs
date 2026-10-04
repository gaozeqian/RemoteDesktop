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
        var candidates = new[]
        {
            home,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            string.IsNullOrEmpty(home) ? string.Empty : Path.Combine(home, "Downloads"),
        };

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

    private static List<FileEntryInfo> ListDrives()
    {
        var drives = new List<FileEntryInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady)
                {
                    drives.Add(new FileEntryInfo(drive.Name, IsDirectory: true, drive.TotalSize, DateTimeOffset.MinValue, FileEntryAttributes.None));
                }
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
