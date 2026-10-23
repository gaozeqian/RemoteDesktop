using RemoteDesktop.Protocol.File;

namespace RemoteDesktop.Features.FileTransfer;

/// <summary>
/// Turns the millisecond timestamps that cross the wire into the only values a local filesystem call can use.
/// </summary>
/// <remarks>
/// The value is chosen by the other computer. <see cref="DateTimeOffset.FromUnixTimeMilliseconds"/> throws
/// <see cref="ArgumentOutOfRangeException"/> outside its own range, which is neither a
/// <see cref="FileOperationException"/> nor an <see cref="IOException"/>, so it used to escape the file channel's
/// error handling and fault the receive loop or a UI command. A timestamp is metadata, never a reason to lose the
/// channel: an unusable one is reported like any other invalid path.
/// </remarks>
internal static class FileTimestamps
{
    private static readonly long Minimum = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    private static readonly long Maximum = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    public static DateTimeOffset FromUnixMilliseconds(long value)
    {
        if (value < Minimum || value > Maximum)
        {
            throw new FileOperationException(FileError.InvalidPath, "The other computer sent an invalid file time.");
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(value);
    }

    /// <summary>
    /// Applies a remote timestamp to a file that is already in place. Best effort: some filesystems accept a smaller
    /// range than <see cref="DateTimeOffset"/> (NTFS starts in 1601), and a file that arrived intact must not be
    /// reported as failed because its cosmetic timestamp could not be set.
    /// </summary>
    public static void TrySetLastWriteTime(string path, DateTimeOffset modified)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IOException or UnauthorizedAccessException)
        {
            // The bytes are already in place; a timestamp the filesystem will not take is not a failed transfer.
        }
    }
}
