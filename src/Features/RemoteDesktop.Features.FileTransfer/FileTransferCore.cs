using System.Buffers;
using System.IO.Hashing;
using RemoteDesktop.Protocol.File;
using RemoteDesktop.Protocol.Framing;

namespace RemoteDesktop.Features.FileTransfer;

/// <summary>Where unfinished transfers are kept, so a later attempt can continue them.</summary>
public static class PartialFile
{
    public const string Extension = ".rdpart";

    /// <summary>
    /// The partial file for a transfer that will end up at <paramref name="finalPath"/>. Size and modification time are
    /// part of the name, so a partial of an older version of the file is never continued by mistake.
    /// </summary>
    /// <remarks>
    /// The suffix adds up to about 40 characters, so a file whose own name is already near the 255-character limit
    /// would get a partial name no filesystem accepts. Such a name is shortened, deterministically and with a hash of
    /// the full name in it so two long names that start alike still get different partials; every name that fits is
    /// left exactly as it was, which keeps partials from earlier versions resumable.
    /// </remarks>
    public static string PathFor(string finalPath, long size, DateTimeOffset modified)
    {
        var suffix = $".{size}-{modified.ToUnixTimeMilliseconds():x}{Extension}";
        var name = Path.GetFileName(finalPath);
        if (name.Length + suffix.Length <= MaxFileNameLength)
        {
            return finalPath + suffix;
        }

        var tag = $"~{XxHash64.HashToUInt64(System.Text.Encoding.UTF8.GetBytes(name)):x16}";
        var keep = MaxFileNameLength - suffix.Length - tag.Length;
        if (keep > 0 && char.IsHighSurrogate(name[keep - 1]))
        {
            keep--; // never cut a surrogate pair in half
        }

        var directory = finalPath[..^name.Length];
        return directory + name[..Math.Max(keep, 0)] + tag + suffix;
    }

    /// <summary>The longest file name the filesystems here accept, in characters.</summary>
    private const int MaxFileNameLength = 255;

    /// <summary>Whether <paramref name="directory"/>'s volume has room for <paramref name="bytes"/> more; true when that cannot be told.</summary>
    public static bool HasRoomFor(string directory, long bytes)
    {
        try
        {
            if (string.IsNullOrEmpty(directory) || bytes <= 0)
            {
                return true;
            }

            var full = Path.GetFullPath(directory);
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var root = VolumeRootFor(full, DriveInfo.GetDrives().Select(drive => drive.Name), comparison) ?? Path.GetPathRoot(full);
            return root is null || new DriveInfo(root).AvailableFreeSpace >= bytes;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return true; // cannot tell (network share); let the write fail if there is really no room
        }
    }

    /// <summary>
    /// The volume <paramref name="fullPath"/> lives on: the longest of <paramref name="volumeRoots"/> (drive letters, or
    /// mount points on Unix) that is a prefix of it, or null when none is. The path root alone is no answer on Unix,
    /// where it is always "/" even for a folder on a USB stick or a separate /home.
    /// </summary>
    internal static string? VolumeRootFor(string fullPath, IEnumerable<string> volumeRoots, StringComparison comparison)
    {
        string? best = null;
        foreach (var root in volumeRoots)
        {
            if (string.IsNullOrEmpty(root) || (best is not null && root.Length <= best.Length))
            {
                continue;
            }

            var prefix = root[^1] == Path.DirectorySeparatorChar ? root : root + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(prefix, comparison) || string.Equals(fullPath, root, comparison))
            {
                best = root;
            }
        }

        return best;
    }

    /// <summary>Picks "name (1).ext", "name (2).ext"... for the first one that does not exist yet.</summary>
    public static string FreeName(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}

/// <summary>
/// Sends the content of a file as chunk frames. The receiver says where to start (bytes it already has); the hash still
/// covers the whole file, so the sender reads the part it skips.
/// </summary>
public static class FileSender
{
    /// <returns>The xxHash64 of the whole file.</returns>
    /// <exception cref="FileOperationException">The file changed in size since it was announced.</exception>
    public static async Task<ulong> SendAsync(Stream wire, string path, long size, long startOffset, Action<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (startOffset < 0 || startOffset > size)
        {
            throw new FileOperationException(FileError.Changed, "The requested start is beyond the end of the file.");
        }

        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan | FileOptions.Asynchronous);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException or IOException)
        {
            throw new FileOperationException(ex is UnauthorizedAccessException ? FileError.AccessDenied : FileError.NotFound, "The file cannot be opened.", ex);
        }

        await using (file.ConfigureAwait(false))
        {
            return await SendOpenAsync(wire, file, size, startOffset, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends an already opened file. The caller owns and disposes <paramref name="file"/>; the host uses this overload
    /// after <see cref="FileAccessPolicy.OpenRead"/> has verified the file behind the handle, so a link swapped after
    /// the path check cannot turn the transfer into a read outside the shared roots.
    /// </summary>
    public static async Task<ulong> SendOpenAsync(Stream wire, FileStream file, long size, long startOffset, Action<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wire);
        ArgumentNullException.ThrowIfNull(file);
        if (startOffset < 0 || startOffset > size)
        {
            throw new FileOperationException(FileError.Changed, "The requested start is beyond the end of the file.");
        }

        if (file.Length < size)
        {
            throw new FileOperationException(FileError.Changed, "The file became smaller during the transfer.");
        }

        var hash = new XxHash64();
        var buffer = ArrayPool<byte>.Shared.Rent(FileChunkFrame.ChunkSize);
        try
        {
            // The part the receiver already has still belongs in the hash.
            for (var remaining = startOffset; remaining > 0;)
            {
                var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new FileOperationException(FileError.Changed, "The file became smaller during the transfer.");
                }

                hash.Append(buffer.AsSpan(0, read));
                remaining -= read;
            }

            var sent = startOffset;
            progress?.Invoke(sent);
            while (sent < size)
            {
                var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(FileChunkFrame.ChunkSize, size - sent)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new FileOperationException(FileError.Changed, "The file became smaller during the transfer.");
                }

                hash.Append(buffer.AsSpan(0, read));
                await FileChunkFrame.WriteAsync(wire, buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                sent += read;
                progress?.Invoke(sent);
            }

            return hash.GetCurrentHashAsUInt64();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>
/// Appends incoming chunk frames to a partial file until it holds the announced number of bytes. An interrupted
/// transfer leaves the partial file behind (flushed) for the next attempt.
/// </summary>
public static class FileReceiver
{
    /// <param name="startOffset">How many bytes <paramref name="partialPath"/> already holds and should keep.</param>
    /// <returns>The xxHash64 of the whole file.</returns>
    /// <exception cref="InvalidDataException">The sender broke the protocol (wrong frame, too much data).</exception>
    /// <exception cref="EndOfStreamException">The sender went away before the file was complete.</exception>
    /// <param name="interpretControl">Turns a non-content frame into the failure it reports (the sender giving up with a reason); without it, any such frame is a protocol error.</param>
    public static async Task<ulong> ReceiveAsync(
        Stream wire,
        string partialPath,
        long size,
        long startOffset,
        Action<long>? progress,
        CancellationToken cancellationToken,
        Func<Frame, FileOperationException?>? interpretControl = null,
        FileAccessPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(wire);

        FileStream file;
        try
        {
            file = policy is null
                ? new FileStream(partialPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 1, FileOptions.Asynchronous)
                : policy.OpenWrite(partialPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new FileOperationException(FileError.AccessDenied, "The destination cannot be written.", ex);
        }
        catch (IOException ex)
        {
            throw new FileOperationException(FileError.Io, $"The destination cannot be written: {ex.Message}", ex);
        }

        await using (file.ConfigureAwait(false))
        {
            if (file.Length < startOffset)
            {
                throw new FileOperationException(FileError.Changed, "The partial file is shorter than announced.");
            }

            // Anything beyond the agreed start is unverified leftovers.
            file.SetLength(startOffset);

            var hash = new XxHash64();
            var buffer = ArrayPool<byte>.Shared.Rent(FileChunkFrame.ChunkSize);
            try
            {
                for (var remaining = startOffset; remaining > 0;)
                {
                    var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        // The partial was longer a moment ago (Length was checked above) and is shorter now: somebody
                        // else is truncating it. Without this the loop would ask for the same bytes for ever.
                        throw new FileOperationException(FileError.Changed, "The partial file became shorter during the transfer.");
                    }

                    hash.Append(buffer.AsSpan(0, read));
                    remaining -= read;
                }

                file.Position = startOffset;
                var received = startOffset;
                progress?.Invoke(received);
                try
                {
                    while (received < size)
                    {
                        var frame = await FrameCodec.ReadAsync(wire, FileChunkFrame.ChunkSize * 2, cancellationToken).ConfigureAwait(false)
                            ?? throw new EndOfStreamException("The sender ended the transfer early.");
                        if (frame.MessageType != FileChunkFrame.MessageType)
                        {
                            throw interpretControl?.Invoke(frame) ?? (Exception)new InvalidDataException("Expected file content.");
                        }

                        if (frame.Payload.Length == 0 || received + frame.Payload.Length > size)
                        {
                            throw new InvalidDataException("The sender sent more data than announced.");
                        }

                        hash.Append(frame.Payload.Span);
                        await file.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
                        received += frame.Payload.Length;
                        progress?.Invoke(received);
                    }
                }
                finally
                {
                    await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }

                return hash.GetCurrentHashAsUInt64();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
