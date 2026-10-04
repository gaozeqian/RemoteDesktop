using RemoteDesktop.Protocol.File;

namespace RemoteDesktop.Features.FileTransfer;

[Flags]
public enum FileEntryAttributes
{
    None = 0,
    Hidden = 1,
    ReadOnly = 2,
    System = 4,
}

public sealed record FileEntryInfo(string Name, bool IsDirectory, long Size, DateTimeOffset Modified, FileEntryAttributes Attributes);

/// <param name="Path">Absolute path as the file system understands it.</param>
/// <param name="Parent">Path of the containing folder; empty at the top (the drive list).</param>
/// <param name="Truncated">The folder has more entries than were returned.</param>
public sealed record DirectoryContents(string Path, string Parent, IReadOnlyList<FileEntryInfo> Entries, bool Truncated);

/// <summary>A starting point for browsing: a drive or a well-known folder.</summary>
public sealed record PlaceInfo(string Name, string Path, bool IsDrive);

/// <summary>A file operation failed in a way the other side can be told about.</summary>
public sealed class FileOperationException : Exception
{
    public FileOperationException()
    {
    }

    public FileOperationException(string message)
        : base(message)
    {
    }

    public FileOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public FileOperationException(FileError error, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    public FileError Error { get; } = FileError.Io;
}
