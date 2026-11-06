using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.File;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Features.FileTransfer;

/// <summary>
/// Host side of file transfer. A channel either serves browsing requests (list, places, new folder, rename, delete)
/// one after another, or carries a single upload or download. Every path is validated by the
/// <see cref="FileAccessPolicy"/>, nothing is served while the <see cref="IUserContextGuard"/> objects, and each
/// operation that changes or moves files is written to the log with the session it came from.
/// </summary>
public sealed partial class FileTransferModule(
    IUserContextGuard guard,
    FileAccessPolicy policy,
    ILogger<FileTransferModule> logger,
    PartialFileLedger? ledger = null) : IFeatureModule
{
    private const int MaxRequestBytes = 64 * 1024;

    public ChannelType ChannelType => ChannelType.FileTransfer;

    public Permissions RequiredPermission => Permissions.FileTransfer;

    public async Task HandleAsync(IChannel channel, SessionContext session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(session);

        var operations = new LocalFileOperations(policy);
        var stream = channel.Stream;
        try
        {
            while (await ProtobufFrames.ReadAsync(stream, FileRequest.Parser, MaxRequestBytes, cancellationToken).ConfigureAwait(false) is { } request)
            {
                if (guard.GetRefusalReason() is { } reason)
                {
                    await RespondAsync(stream, Failure(new FileOperationException(FileError.NotAllowed, reason)), cancellationToken).ConfigureAwait(false);
                    return;
                }

                switch (request.BodyCase)
                {
                    case FileRequest.BodyOneofCase.List:
                        await RespondAsync(stream, List(operations, request.List.Path), cancellationToken).ConfigureAwait(false);
                        break;
                    case FileRequest.BodyOneofCase.Places:
                        await RespondAsync(stream, Places(operations), cancellationToken).ConfigureAwait(false);
                        break;
                    case FileRequest.BodyOneofCase.MakeDirectory:
                        LogOperation(logger, session.Id, "mkdir", request.MakeDirectory.Path);
                        await RespondAsync(stream, Run(() => operations.MakeDirectory(request.MakeDirectory.Path)), cancellationToken).ConfigureAwait(false);
                        break;
                    case FileRequest.BodyOneofCase.Rename:
                        LogRename(logger, session.Id, request.Rename.Path, request.Rename.NewName);
                        await RespondAsync(stream, Run(() => operations.Rename(request.Rename.Path, request.Rename.NewName)), cancellationToken).ConfigureAwait(false);
                        break;
                    case FileRequest.BodyOneofCase.Delete:
                        LogOperation(logger, session.Id, request.Delete.Recursive ? "delete (recursive)" : "delete", request.Delete.Path);
                        await RespondAsync(stream, Run(() => operations.Delete(request.Delete.Path, request.Delete.Recursive)), cancellationToken).ConfigureAwait(false);
                        break;
                    case FileRequest.BodyOneofCase.Download:
                        await DownloadAsync(stream, request.Download, session, cancellationToken).ConfigureAwait(false);
                        return;
                    case FileRequest.BodyOneofCase.Upload:
                        await UploadAsync(stream, request.Upload, session, cancellationToken).ConfigureAwait(false);
                        return;
                    default:
                        await RespondAsync(stream, Failure(new FileOperationException(FileError.Unsupported, "Unexpected request.")), cancellationToken).ConfigureAwait(false);
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or OperationCanceledException or ObjectDisposedException)
        {
            // The controller went away or sent garbage; whatever was unfinished stays resumable.
        }
    }

    private async Task DownloadAsync(Stream stream, DownloadRequest request, SessionContext session, CancellationToken cancellationToken)
    {
        string path;
        FileStream file;
        long size;
        DateTimeOffset modified;
        try
        {
            path = policy.Normalize(request.Path);
            // The handle is verified against the roots after it is open, then used for both the size and every byte
            // sent. A link swapped between the path check and this open cannot redirect the transfer.
            file = policy.OpenRead(path);
            size = file.Length;
            modified = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
        }
        catch (Exception ex) when (ex is FileOperationException or IOException or UnauthorizedAccessException)
        {
            var failure = ex as FileOperationException ?? new FileOperationException(FileError.Io, "The file cannot be read.", ex);
            await RespondAsync(stream, Failure(failure), cancellationToken).ConfigureAwait(false);
            return;
        }

        await using (file.ConfigureAwait(false))
        {
            await RespondAsync(stream, new FileResponse
            {
                DownloadAccepted = new DownloadAccepted { Status = Ok(), Size = (ulong)size, ModifiedUnixMs = modified.ToUnixTimeMilliseconds() },
            }, cancellationToken).ConfigureAwait(false);

            // The receiver decides where to continue, based on what it already has.
            var start = await ProtobufFrames.ReadAsync(stream, FileRequest.Parser, MaxRequestBytes, cancellationToken).ConfigureAwait(false);
            if (start?.BodyCase != FileRequest.BodyOneofCase.Start)
            {
                return; // skipped by the controller's conflict policy, or it gave up
            }

            LogTransfer(logger, session.Id, "download", path, size, (long)start.Start.Offset);
            try
            {
                var hash = await FileSender.SendOpenAsync(stream, file, size, (long)start.Start.Offset, null, cancellationToken).ConfigureAwait(false);
                await RespondAsync(stream, new FileResponse { Complete = new TransferComplete { Status = Ok(), Hash = hash, Size = (ulong)size } }, cancellationToken).ConfigureAwait(false);
                LogTransferDone(logger, session.Id, "download", path);
            }
            catch (FileOperationException ex)
            {
                await RespondAsync(stream, new FileResponse { Complete = new TransferComplete { Status = StatusOf(ex) } }, cancellationToken).ConfigureAwait(false);
                LogTransferFailed(logger, session.Id, "download", path, ex.Message);
            }
        }
    }

    private async Task UploadAsync(Stream stream, UploadRequest request, SessionContext session, CancellationToken cancellationToken)
    {
        string directory;
        string name;
        try
        {
            directory = policy.Normalize(request.Directory);
            name = FileAccessPolicy.NormalizeName(request.FileName);
            if (!Directory.Exists(directory))
            {
                throw new FileOperationException(FileError.NotFound, "The destination folder does not exist.");
            }
        }
        catch (FileOperationException ex)
        {
            await RespondAsync(stream, Failure(ex), cancellationToken).ConfigureAwait(false);
            return;
        }

        // The size is the other computer's to announce. Cast unchecked, anything above long.MaxValue turns negative,
        // which skips the free-space check below and then "receives" an empty file.
        if (request.Size > long.MaxValue)
        {
            await RespondAsync(stream, Failure(new FileOperationException(FileError.InvalidPath, "The announced file size is not valid.")), cancellationToken).ConfigureAwait(false);
            return;
        }

        var size = (long)request.Size;
        DateTimeOffset modified;
        try
        {
            modified = FileTimestamps.FromUnixMilliseconds(request.ModifiedUnixMs);
        }
        catch (FileOperationException ex)
        {
            await RespondAsync(stream, Failure(ex), cancellationToken).ConfigureAwait(false);
            return;
        }

        var finalPath = Path.Combine(directory, name);
        var overwrite = request.ConflictPolicy == ConflictPolicy.Overwrite;
        if (Directory.Exists(finalPath))
        {
            await RespondAsync(stream, Failure(new FileOperationException(FileError.AlreadyExists, "A folder with that name exists.")), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (File.Exists(finalPath))
        {
            if (request.ConflictPolicy == ConflictPolicy.Skip)
            {
                await RespondAsync(stream, new FileResponse { UploadAccepted = new UploadAccepted { Status = new FileStatus { Error = FileError.Skipped, Message = "Already exists." }, FinalPath = finalPath } }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (request.ConflictPolicy == ConflictPolicy.Rename)
            {
                finalPath = PartialFile.FreeName(finalPath);
            }
        }

        var partial = PartialFile.PathFor(finalPath, size, modified);

        // Written down before the first byte is received: a process that is killed halfway through has already left
        // the record, which is the only way this file is ever found again if nobody comes back for it.
        ledger?.Record(partial);

        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > size)
        {
            // A partial of something else entirely (the file shrank since). Not worth keeping, so not worth tracking.
            File.Delete(partial);
            ledger?.Forget(partial);
            offset = 0;
        }

        // The partial is turned into the final file by copying it (see FinalizeAsync), so for a moment both exist:
        // what is still to arrive plus a second copy of the whole. Asking now fails before the transfer, not after it.
        var needed = size > long.MaxValue / 2 ? long.MaxValue : (2 * size) - offset;
        if (!PartialFile.HasRoomFor(directory, needed))
        {
            await RespondAsync(stream, Failure(new FileOperationException(FileError.Io, "There is not enough free space at the destination.")), cancellationToken).ConfigureAwait(false);
            return;
        }

        await RespondAsync(stream, new FileResponse { UploadAccepted = new UploadAccepted { Status = Ok(), FinalPath = finalPath, ResumeOffset = (ulong)offset } }, cancellationToken).ConfigureAwait(false);
        LogTransfer(logger, session.Id, "upload", finalPath, size, offset);

        try
        {
            var hash = await FileReceiver.ReceiveAsync(stream, partial, size, offset, null, cancellationToken, policy: policy).ConfigureAwait(false);
            var finish = await ProtobufFrames.ReadAsync(stream, FileRequest.Parser, MaxRequestBytes, cancellationToken).ConfigureAwait(false);
            if (finish?.BodyCase != FileRequest.BodyOneofCase.Finish)
            {
                throw new InvalidDataException("The sender did not finish the transfer.");
            }

            if (finish.Finish.Hash != hash)
            {
                // The partial file cannot be trusted, including whatever it held before this attempt.
                File.Delete(partial);
                ledger?.Forget(partial);
                await RespondAsync(stream, new FileResponse { Complete = new TransferComplete { Status = new FileStatus { Error = FileError.HashMismatch, Message = "The file arrived damaged." } } }, cancellationToken).ConfigureAwait(false);
                LogTransferFailed(logger, session.Id, "upload", finalPath, "hash mismatch");
                return;
            }

            finalPath = await FinalizeAsync(partial, finalPath, overwrite, modified, cancellationToken).ConfigureAwait(false);
            await RespondAsync(stream, new FileResponse { Complete = new TransferComplete { Status = Ok(), Hash = hash, Size = (ulong)size } }, cancellationToken).ConfigureAwait(false);
            LogTransferDone(logger, session.Id, "upload", finalPath);
        }
        catch (FileOperationException ex)
        {
            await RespondAsync(stream, new FileResponse { Complete = new TransferComplete { Status = StatusOf(ex) } }, cancellationToken).ConfigureAwait(false);
            LogTransferFailed(logger, session.Id, "upload", finalPath, ex.Message);
        }
    }

    /// <summary>
    /// Turns the verified partial into the final file. Every byte is copied into a file opened and checked under the
    /// policy: a handle cannot be redirected, so the bytes can only land in a file whose real target passed the root
    /// check, which a path-based rename cannot promise (a link swapped after Normalize would redirect it).
    /// </summary>
    /// <remarks>
    /// A failure leaves the partial in place so the transfer can be finished later, and leaves nothing half-written
    /// under the final name. Replacing an existing file goes through a temporary file next to it, so a disk that
    /// fills up or a session that ends in the middle never costs the user the file that was there.
    /// </remarks>
    /// <exception cref="FileOperationException">The file could not be put in place (disk full, access denied, name taken too often).</exception>
    private async Task<string> FinalizeAsync(string partial, string finalPath, bool overwrite, DateTimeOffset modified, CancellationToken cancellationToken)
    {
        try
        {
            finalPath = overwrite
                ? await ReplaceAsync(partial, finalPath, cancellationToken).ConfigureAwait(false)
                : await CreateAsync(partial, finalPath, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new FileOperationException(FileError.AccessDenied, "The file could not be put in place.", ex);
        }
        catch (IOException ex)
        {
            // A full disk surfaces here as a plain IOException; without the conversion it would escape the upload's
            // error reporting and the controller would only see the channel close.
            throw new FileOperationException(FileError.Io, $"The file could not be put in place: {ex.Message}", ex);
        }

        // The file is in place; the partial has done its job. If it cannot be removed it stays on the ledger and the
        // sweeper takes it later, which is no reason to report a finished transfer as failed.
        try
        {
            File.Delete(partial);
            ledger?.Forget(partial);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogPartialKept(logger, partial, ex.Message);
        }

        FileTimestamps.TrySetLastWriteTime(finalPath, modified);
        return finalPath;
    }

    /// <summary>Copies the partial to a name that is not taken, moving to the next free name if somebody takes it first.</summary>
    private async Task<string> CreateAsync(string partial, string finalPath, CancellationToken cancellationToken)
    {
        await using var source = policy.OpenRead(partial);
        FileStream destination;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                destination = policy.OpenWrite(finalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                break;
            }
            catch (FileOperationException ex) when (ex.Error == FileError.AlreadyExists && attempt < 100)
            {
                // Somebody created that name in the meantime: keep both and try the next free name.
                finalPath = PartialFile.FreeName(finalPath);
            }
        }

        try
        {
            await CopyAsync(source, destination, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // CreateNew made this file, so it is ours to remove: a half-written file under the final name would pass
            // for the real one.
            TryDelete(finalPath);
            throw;
        }

        return finalPath;
    }

    /// <summary>Copies the partial to a temporary file in the same folder and then swaps it over <paramref name="finalPath"/>.</summary>
    private async Task<string> ReplaceAsync(string partial, string finalPath, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(finalPath) ?? string.Empty, $".rd-{Guid.NewGuid():N}{PartialFile.Extension}");

        // Written down before the first byte, like the partial itself: a process killed in the middle leaves a file
        // nobody would otherwise find again.
        ledger?.Record(temporary);
        try
        {
            await using (var source = policy.OpenRead(partial))
            {
                var destination = policy.OpenWrite(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await CopyAsync(source, destination, cancellationToken).ConfigureAwait(false);
            }

            // The swap is by path, so the name is judged once more right before it is taken over. The bytes themselves
            // were written through a verified handle in this folder; what remains is the instant between this check and
            // the rename, which only a process that can already rewrite the shared folder could use.
            policy.Normalize(finalPath);
            File.Move(temporary, finalPath, overwrite: true);
        }
        catch
        {
            // Still on the ledger if it could not be removed, so the sweeper gets another go at it.
            if (TryDelete(temporary))
            {
                ledger?.Forget(temporary);
            }

            throw;
        }

        ledger?.Forget(temporary);
        return finalPath;
    }

    private static async Task CopyAsync(Stream source, FileStream destination, CancellationToken cancellationToken)
    {
        await using (destination.ConfigureAwait(false))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the error that got us here is the one worth reporting.
            return false;
        }
    }

    private static FileResponse List(LocalFileOperations operations, string path)
    {
        try
        {
            var contents = operations.List(path);
            var listing = new DirectoryListing { Status = Ok(), Path = contents.Path, Parent = contents.Parent, Truncated = contents.Truncated };
            listing.Entries.Add(contents.Entries.Select(e => new FileEntry
            {
                Name = e.Name,
                IsDirectory = e.IsDirectory,
                Size = (ulong)Math.Max(0, e.Size),
                ModifiedUnixMs = e.Modified == DateTimeOffset.MinValue ? 0 : e.Modified.ToUnixTimeMilliseconds(),
                Attributes = (uint)e.Attributes,
            }));
            return new FileResponse { Listing = listing };
        }
        catch (FileOperationException ex)
        {
            return new FileResponse { Listing = new DirectoryListing { Status = StatusOf(ex) } };
        }
    }

    private static FileResponse Places(LocalFileOperations operations)
    {
        var places = new PlacesResponse { Status = Ok() };
        places.Places.Add(operations.GetPlaces().Select(p => new Place { Name = p.Name, Path = p.Path, IsDrive = p.IsDrive }));
        return new FileResponse { Places = places };
    }

    private static FileResponse Run(Action action)
    {
        try
        {
            action();
            return new FileResponse { Result = new OperationResult { Status = Ok() } };
        }
        catch (FileOperationException ex)
        {
            return Failure(ex);
        }
    }

    /// <summary>
    /// A path or name for the log. Both come from the other computer, and the path policy lets control characters
    /// through (a file may legitimately be named that way), so a newline in one could forge a log line. The value is
    /// logged with such characters spelled out instead; nothing is refused because of it.
    /// </summary>
    internal static string Printable(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Any(char.IsControl))
        {
            return text ?? string.Empty;
        }

        var builder = new System.Text.StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                case '\t':
                    builder.Append(@"\t");
                    break;
                case var control when char.IsControl(control):
                    builder.Append(@"\u").Append(((int)control).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>A path or name from the other computer, shown in the log through <see cref="Printable"/>.</summary>
    private readonly record struct LogText(string? Value)
    {
        public static implicit operator LogText(string? value) => new(value);

        public override string ToString() => Printable(Value);
    }

    private static FileResponse Failure(FileOperationException ex) => new() { Result = new OperationResult { Status = StatusOf(ex) } };

    private static FileStatus Ok() => new() { Error = FileError.None };

    private static FileStatus StatusOf(FileOperationException ex) => new() { Error = ex.Error, Message = ex.Message };

    private static ValueTask RespondAsync(Stream stream, FileResponse response, CancellationToken cancellationToken) =>
        ProtobufFrames.WriteAsync(stream, response, cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: {Operation} {Path}")]
    private static partial void LogOperation(ILogger logger, SessionId sessionId, string operation, LogText path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: rename {Path} to {NewName}")]
    private static partial void LogRename(ILogger logger, SessionId sessionId, LogText path, LogText newName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: {Operation} of {Path} started ({Size} bytes, from offset {Offset}).")]
    private static partial void LogTransfer(ILogger logger, SessionId sessionId, string operation, LogText path, long size, long offset);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: {Operation} of {Path} finished.")]
    private static partial void LogTransferDone(ILogger logger, SessionId sessionId, string operation, LogText path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Kept the finished upload's partial file {Path}: {Reason}")]
    private static partial void LogPartialKept(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId}: {Operation} of {Path} failed: {Reason}")]
    private static partial void LogTransferFailed(ILogger logger, SessionId sessionId, string operation, LogText path, string reason);
}
