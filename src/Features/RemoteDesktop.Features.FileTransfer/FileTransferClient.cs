using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.File;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Features.FileTransfer;

/// <summary>Told what a single-file transfer is doing, as it does it.</summary>
public interface ITransferObserver
{
    /// <summary>The transfer is agreed: how big the file is, how much is already there, and where the unfinished and final files are (on the receiving side).</summary>
    void Started(long totalBytes, long startOffset, string finalPath, string partialPath);

    void Progress(long bytes);
}

/// <summary>How a single-file transfer ended when it did not fail.</summary>
public enum TransferOutcome
{
    Completed,

    /// <summary>The conflict policy said not to copy a file that already exists.</summary>
    Skipped,
}

/// <summary>
/// Controller side of one file transfer over its own channel: uploads send a local file to the host, downloads fetch a
/// host file. Both continue from what an earlier attempt left, verify the whole file with a hash and put it in place only
/// when it is complete.
/// </summary>
public static class FileTransferClient
{
    private const int MaxResponseBytes = 64 * 1024;

    public static async Task<TransferOutcome> UploadAsync(
        IPeerConnection connection,
        string localPath,
        string remoteDirectory,
        string fileName,
        ConflictPolicy policy,
        ITransferObserver observer,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(observer);

        FileInfo info;
        try
        {
            info = new FileInfo(localPath);
            if (!info.Exists)
            {
                throw new FileOperationException(FileError.NotFound, "The file does not exist.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new FileOperationException(FileError.AccessDenied, "The file cannot be read.", ex);
        }

        // A damaged partial on the host is deleted by the host; the second attempt then starts from nothing.
        for (var attempt = 0; ; attempt++)
        {
            var (outcome, retry) = await UploadOnceAsync(connection, info, remoteDirectory, fileName, policy, observer, requestTimeout, cancellationToken).ConfigureAwait(false);
            if (!retry || attempt > 0)
            {
                return outcome;
            }
        }
    }

    /// <summary>
    /// How long an upload waits for the host's verdict after the last byte. Unlike every other answer in a transfer,
    /// this one is not immediate: the host copies the whole verified partial file to its final name first, which takes
    /// time in proportion to the file. The plain deadline would report a failure for a transfer the host then goes on
    /// to complete. The allowance assumes the host's disk copies at no less than 1 MiB/s, and stops at an hour so a
    /// host that has really gone quiet is still given up on.
    /// </summary>
    internal static TimeSpan FinishTimeout(TimeSpan requestTimeout, long size)
    {
        var extra = TimeSpan.FromSeconds(Math.Max(0, size) / (double)(1024 * 1024));
        var ceiling = TimeSpan.FromHours(1);
        return requestTimeout >= ceiling ? requestTimeout : TimeSpan.FromTicks(Math.Min((requestTimeout + extra).Ticks, ceiling.Ticks));
    }

    public static async Task<TransferOutcome> DownloadAsync(
        IPeerConnection connection,
        string remotePath,
        string localDirectory,
        string fileName,
        ConflictPolicy policy,
        ITransferObserver observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(observer);

        var destination = Path.Combine(localDirectory, FileAccessPolicy.NormalizeName(fileName));
        for (var attempt = 0; ; attempt++)
        {
            var (outcome, retry) = await DownloadOnceAsync(connection, remotePath, destination, policy, observer, cancellationToken).ConfigureAwait(false);
            if (!retry || attempt > 0)
            {
                return outcome;
            }
        }
    }

    private static async Task<(TransferOutcome Outcome, bool Retry)> UploadOnceAsync(
        IPeerConnection connection,
        FileInfo info,
        string remoteDirectory,
        string fileName,
        ConflictPolicy policy,
        ITransferObserver observer,
        TimeSpan? requestTimeout,
        CancellationToken cancellationToken)
    {
        var size = info.Length;
        var modified = new DateTimeOffset(info.LastWriteTimeUtc);
        var channel = await connection.OpenChannelAsync(ChannelType.FileTransfer, bidirectional: true, cancellationToken).ConfigureAwait(false);
        await using (channel.ConfigureAwait(false))
        {
            var stream = channel.Stream;
            try
            {
                await ProtobufFrames.WriteAsync(stream, new FileRequest
                {
                    Upload = new UploadRequest
                    {
                        Directory = remoteDirectory,
                        FileName = fileName,
                        Size = (ulong)size,
                        ModifiedUnixMs = modified.ToUnixTimeMilliseconds(),
                        ConflictPolicy = policy,
                    },
                }, cancellationToken).ConfigureAwait(false);

                var answer = await RequestTimeout.RunAsync(token => ReadResponseAsync(stream, token), cancellationToken, requestTimeout).ConfigureAwait(false);
                if (answer.BodyCase == FileResponse.BodyOneofCase.Result)
                {
                    Check(answer.Result.Status);
                }

                if (answer.BodyCase != FileResponse.BodyOneofCase.UploadAccepted)
                {
                    throw new FileOperationException(FileError.Unsupported, "The remote computer answered unexpectedly.");
                }

                var accepted = answer.UploadAccepted;
                if (accepted.Status.Error == FileError.Skipped)
                {
                    return (TransferOutcome.Skipped, false);
                }

                Check(accepted.Status);
                var offset = ToLength(accepted.ResumeOffset);
                if (offset > size)
                {
                    throw new FileOperationException(FileError.Unsupported, "The remote computer asked to continue beyond the end of the file.");
                }

                observer.Started(size, offset, accepted.FinalPath, PartialFile.PathFor(accepted.FinalPath, size, modified));

                var hash = await FileSender.SendAsync(stream, info.FullName, size, offset, observer.Progress, cancellationToken).ConfigureAwait(false);
                await ProtobufFrames.WriteAsync(stream, new FileRequest { Finish = new TransferComplete { Status = new FileStatus(), Hash = hash, Size = (ulong)size } }, cancellationToken).ConfigureAwait(false);

                var verdict = await RequestTimeout.RunAsync(
                    token => ReadResponseAsync(stream, token),
                    cancellationToken,
                    FinishTimeout(requestTimeout ?? RequestTimeout.Default, size)).ConfigureAwait(false);
                if (verdict.BodyCase != FileResponse.BodyOneofCase.Complete)
                {
                    throw new FileOperationException(FileError.Unsupported, "The remote computer answered unexpectedly.");
                }

                if (verdict.Complete.Status.Error == FileError.HashMismatch && offset > 0)
                {
                    return (TransferOutcome.Completed, true);
                }

                Check(verdict.Complete.Status);
                return (TransferOutcome.Completed, false);
            }
            catch (TimeoutException ex)
            {
                // The host took the channel and then stopped answering: the transfer never started, or never ended.
                throw new FileOperationException(FileError.Io, ex.Message, ex);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException && !cancellationToken.IsCancellationRequested)
            {
                throw new FileOperationException(FileError.Io, "The connection to the remote computer failed.", ex);
            }
        }
    }

    private static async Task<(TransferOutcome Outcome, bool Retry)> DownloadOnceAsync(
        IPeerConnection connection,
        string remotePath,
        string destination,
        ConflictPolicy policy,
        ITransferObserver observer,
        CancellationToken cancellationToken)
    {
        var channel = await connection.OpenChannelAsync(ChannelType.FileTransfer, bidirectional: true, cancellationToken).ConfigureAwait(false);
        await using (channel.ConfigureAwait(false))
        {
            var stream = channel.Stream;
            try
            {
                await ProtobufFrames.WriteAsync(stream, new FileRequest { Download = new DownloadRequest { Path = remotePath } }, cancellationToken).ConfigureAwait(false);
                var answer = await RequestTimeout.RunAsync(token => ReadResponseAsync(stream, token), cancellationToken).ConfigureAwait(false);
                if (answer.BodyCase == FileResponse.BodyOneofCase.Result)
                {
                    Check(answer.Result.Status);
                }

                if (answer.BodyCase != FileResponse.BodyOneofCase.DownloadAccepted)
                {
                    throw new FileOperationException(FileError.Unsupported, "The remote computer answered unexpectedly.");
                }

                var accepted = answer.DownloadAccepted;
                Check(accepted.Status);
                var size = ToLength(accepted.Size);
                var modified = FileTimestamps.FromUnixMilliseconds(accepted.ModifiedUnixMs);

                var final = destination;
                if (Directory.Exists(final))
                {
                    throw new FileOperationException(FileError.AlreadyExists, "A folder with that name exists.");
                }

                if (File.Exists(final))
                {
                    if (policy == ConflictPolicy.Skip)
                    {
                        return (TransferOutcome.Skipped, false); // closing the channel without a start tells the host to stop
                    }

                    if (policy == ConflictPolicy.Rename)
                    {
                        final = PartialFile.FreeName(final);
                    }
                }

                var partial = PartialFile.PathFor(final, size, modified);
                var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (offset > size)
                {
                    File.Delete(partial);
                    offset = 0;
                }

                if (!PartialFile.HasRoomFor(Path.GetDirectoryName(final) ?? string.Empty, size - offset))
                {
                    throw new FileOperationException(FileError.Io, "There is not enough free space at the destination.");
                }

                observer.Started(size, offset, final, partial);
                await ProtobufFrames.WriteAsync(stream, new FileRequest { Start = new TransferStart { Offset = (ulong)offset } }, cancellationToken).ConfigureAwait(false);

                var hash = await FileReceiver.ReceiveAsync(stream, partial, size, offset, observer.Progress, cancellationToken, InterpretControl).ConfigureAwait(false);
                var verdict = await RequestTimeout.RunAsync(token => ReadResponseAsync(stream, token), cancellationToken).ConfigureAwait(false);
                if (verdict.BodyCase != FileResponse.BodyOneofCase.Complete)
                {
                    throw new FileOperationException(FileError.Unsupported, "The remote computer answered unexpectedly.");
                }

                Check(verdict.Complete.Status);
                if (verdict.Complete.Hash != hash)
                {
                    // Nothing in the partial can be trusted, including what earlier attempts left.
                    File.Delete(partial);
                    return offset > 0
                        ? (TransferOutcome.Completed, true)
                        : throw new FileOperationException(FileError.HashMismatch, "The file arrived damaged.");
                }

                Place(partial, final, policy == ConflictPolicy.Overwrite, modified);
                return (TransferOutcome.Completed, false);
            }
            catch (TimeoutException ex)
            {
                // The host took the channel and then stopped answering: the transfer never started, or never ended.
                throw new FileOperationException(FileError.Io, ex.Message, ex);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException && !cancellationToken.IsCancellationRequested)
            {
                throw new FileOperationException(FileError.Io, $"The transfer failed: {ex.Message}", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new FileOperationException(FileError.AccessDenied, "The destination cannot be written.", ex);
            }
        }
    }

    // The sender reports a problem it hit mid-transfer (file vanished, changed) as a closing message instead of more data.
    private static FileOperationException? InterpretControl(Frame frame)
    {
        if (frame.MessageType != ProtobufFrames.EnvelopeMessageType)
        {
            return null;
        }

        try
        {
            var response = FileResponse.Parser.ParseFrom(frame.Payload.Span);
            return response is { BodyCase: FileResponse.BodyOneofCase.Complete, Complete.Status.Error: not FileError.None }
                ? new FileOperationException(response.Complete.Status.Error, response.Complete.Status.Message)
                : null;
        }
        catch (Google.Protobuf.InvalidProtocolBufferException)
        {
            return null;
        }
    }

    // Moving the finished partial into place is what makes the file appear, so a half-received file is never visible under its name.
    private static void Place(string partial, string final, bool overwrite, DateTimeOffset modified)
    {
        try
        {
            File.Move(partial, final, overwrite);
        }
        catch (IOException) when (!overwrite && File.Exists(final))
        {
            // Somebody created that name in the meantime: keep both.
            final = PartialFile.FreeName(final);
            File.Move(partial, final, overwrite: false);
        }

        FileTimestamps.TrySetLastWriteTime(final, modified);
    }

    private static async Task<FileResponse> ReadResponseAsync(Stream stream, CancellationToken cancellationToken) =>
        await ProtobufFrames.ReadAsync(stream, FileResponse.Parser, MaxResponseBytes, cancellationToken).ConfigureAwait(false)
        ?? throw new EndOfStreamException("The remote computer closed the channel.");

    // Sizes and offsets are the host's to announce; cast unchecked, anything above long.MaxValue turns negative.
    private static long ToLength(ulong announced) =>
        announced <= long.MaxValue
            ? (long)announced
            : throw new FileOperationException(FileError.Unsupported, "The remote computer announced a size that is not valid.");

    private static void Check(FileStatus? status)
    {
        if (status is { Error: not FileError.None })
        {
            throw new FileOperationException(status.Error, string.IsNullOrEmpty(status.Message) ? status.Error.ToString() : status.Message);
        }
    }
}
