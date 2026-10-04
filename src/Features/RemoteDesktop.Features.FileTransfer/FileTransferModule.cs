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
/// <see cref="FileAccessPolicy"/>, nothing is served while the <see cref="IFileAccessGuard"/> objects, and each
/// operation that changes or moves files is written to the log with the session it came from.
/// </summary>
public sealed partial class FileTransferModule(
    IFileAccessGuard guard,
    FileAccessPolicy policy,
    ILogger<FileTransferModule> logger) : IFeatureModule
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
        FileInfo info;
        try
        {
            path = policy.Normalize(request.Path);
            info = new FileInfo(path);
            if (!info.Exists)
            {
                throw Directory.Exists(path)
                    ? new FileOperationException(FileError.InvalidPath, "That is a folder; download its files one by one.")
                    : new FileOperationException(FileError.NotFound, "The file does not exist.");
            }
        }
        catch (FileOperationException ex)
        {
            await RespondAsync(stream, Failure(ex), cancellationToken).ConfigureAwait(false);
            return;
        }

        var size = info.Length;
        var modified = info.LastWriteTimeUtc;
        await RespondAsync(stream, new FileResponse
        {
            DownloadAccepted = new DownloadAccepted { Status = Ok(), Size = (ulong)size, ModifiedUnixMs = new DateTimeOffset(modified).ToUnixTimeMilliseconds() },
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
            var hash = await FileSender.SendAsync(stream, path, size, (long)start.Start.Offset, null, cancellationToken).ConfigureAwait(false);
            await RespondAsync(stream, new FileResponse { Complete = new TransferComplete { Status = Ok(), Hash = hash, Size = (ulong)size } }, cancellationToken).ConfigureAwait(false);
            LogTransferDone(logger, session.Id, "download", path);
        }
        catch (FileOperationException ex)
        {
            await RespondAsync(stream, new FileResponse { Complete = new TransferComplete { Status = StatusOf(ex) } }, cancellationToken).ConfigureAwait(false);
            LogTransferFailed(logger, session.Id, "download", path, ex.Message);
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

        var size = (long)request.Size;
        var modified = DateTimeOffset.FromUnixTimeMilliseconds(request.ModifiedUnixMs);
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
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > size)
        {
            File.Delete(partial);
            offset = 0;
        }

        if (!PartialFile.HasRoomFor(directory, size - offset))
        {
            await RespondAsync(stream, Failure(new FileOperationException(FileError.Io, "There is not enough free space at the destination.")), cancellationToken).ConfigureAwait(false);
            return;
        }

        await RespondAsync(stream, new FileResponse { UploadAccepted = new UploadAccepted { Status = Ok(), FinalPath = finalPath, ResumeOffset = (ulong)offset } }, cancellationToken).ConfigureAwait(false);
        LogTransfer(logger, session.Id, "upload", finalPath, size, offset);

        try
        {
            var hash = await FileReceiver.ReceiveAsync(stream, partial, size, offset, null, cancellationToken).ConfigureAwait(false);
            var finish = await ProtobufFrames.ReadAsync(stream, FileRequest.Parser, MaxRequestBytes, cancellationToken).ConfigureAwait(false);
            if (finish?.BodyCase != FileRequest.BodyOneofCase.Finish)
            {
                throw new InvalidDataException("The sender did not finish the transfer.");
            }

            if (finish.Finish.Hash != hash)
            {
                // The partial file cannot be trusted, including whatever it held before this attempt.
                File.Delete(partial);
                await RespondAsync(stream, new FileResponse { Complete = new TransferComplete { Status = new FileStatus { Error = FileError.HashMismatch, Message = "The file arrived damaged." } } }, cancellationToken).ConfigureAwait(false);
                LogTransferFailed(logger, session.Id, "upload", finalPath, "hash mismatch");
                return;
            }

            Finalize(partial, ref finalPath, overwrite, modified);
            await RespondAsync(stream, new FileResponse { Complete = new TransferComplete { Status = Ok(), Hash = hash, Size = (ulong)size } }, cancellationToken).ConfigureAwait(false);
            LogTransferDone(logger, session.Id, "upload", finalPath);
        }
        catch (FileOperationException ex)
        {
            await RespondAsync(stream, new FileResponse { Complete = new TransferComplete { Status = StatusOf(ex) } }, cancellationToken).ConfigureAwait(false);
            LogTransferFailed(logger, session.Id, "upload", finalPath, ex.Message);
        }
    }

    // Moving the finished partial into place is what makes the file appear, so a half-received file is never visible under its name.
    private static void Finalize(string partial, ref string finalPath, bool overwrite, DateTimeOffset modified)
    {
        try
        {
            File.Move(partial, finalPath, overwrite);
        }
        catch (IOException) when (!overwrite && File.Exists(finalPath))
        {
            // Somebody created that name in the meantime: keep both.
            finalPath = PartialFile.FreeName(finalPath);
            File.Move(partial, finalPath, overwrite: false);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new FileOperationException(FileError.AccessDenied, "The file could not be put in place.", ex);
        }

        File.SetLastWriteTimeUtc(finalPath, modified.UtcDateTime);
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

    private static FileResponse Failure(FileOperationException ex) => new() { Result = new OperationResult { Status = StatusOf(ex) } };

    private static FileStatus Ok() => new() { Error = FileError.None };

    private static FileStatus StatusOf(FileOperationException ex) => new() { Error = ex.Error, Message = ex.Message };

    private static ValueTask RespondAsync(Stream stream, FileResponse response, CancellationToken cancellationToken) =>
        ProtobufFrames.WriteAsync(stream, response, cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: {Operation} {Path}")]
    private static partial void LogOperation(ILogger logger, SessionId sessionId, string operation, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: rename {Path} to {NewName}")]
    private static partial void LogRename(ILogger logger, SessionId sessionId, string path, string newName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: {Operation} of {Path} started ({Size} bytes, from offset {Offset}).")]
    private static partial void LogTransfer(ILogger logger, SessionId sessionId, string operation, string path, long size, long offset);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: {Operation} of {Path} finished.")]
    private static partial void LogTransferDone(ILogger logger, SessionId sessionId, string operation, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId}: {Operation} of {Path} failed: {Reason}")]
    private static partial void LogTransferFailed(ILogger logger, SessionId sessionId, string operation, string path, string reason);
}
