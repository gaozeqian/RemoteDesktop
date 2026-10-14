using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.File;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Features.FileTransfer;

/// <summary>
/// One side of the file manager: a computer whose folders can be browsed and changed. Both panes use the same
/// contract, so the UI does not care which one is local. Failures surface as <see cref="FileOperationException"/>.
/// </summary>
public interface IFileSource
{
    /// <summary>Lists a folder; an empty path lists the drives.</summary>
    Task<DirectoryContents> ListAsync(string path, CancellationToken cancellationToken);

    Task<IReadOnlyList<PlaceInfo>> GetPlacesAsync(CancellationToken cancellationToken);

    Task MakeDirectoryAsync(string path, CancellationToken cancellationToken);

    Task RenameAsync(string path, string newName, CancellationToken cancellationToken);

    Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken);

    /// <summary>Joins a folder and an item name the way this computer writes paths.</summary>
    string Combine(string folder, string name);
}

/// <summary>This computer's files, for the local pane of the file manager.</summary>
public sealed class LocalFileSource : IFileSource
{
    private readonly LocalFileOperations _operations = new(FileAccessPolicy.Unrestricted);

    public Task<DirectoryContents> ListAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() => _operations.List(path), cancellationToken);

    public Task<IReadOnlyList<PlaceInfo>> GetPlacesAsync(CancellationToken cancellationToken) =>
        Task.Run(_operations.GetPlaces, cancellationToken);

    public Task MakeDirectoryAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() => _operations.MakeDirectory(path), cancellationToken);

    public Task RenameAsync(string path, string newName, CancellationToken cancellationToken) =>
        Task.Run(() => _operations.Rename(path, newName), cancellationToken);

    public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken) =>
        Task.Run(() => _operations.Delete(path, recursive), cancellationToken);

    public string Combine(string folder, string name) => string.IsNullOrEmpty(folder) ? name : Path.Combine(folder, name);
}

/// <summary>
/// The other computer's files, asked for over one long-lived channel. The channel is opened on first use and reopened
/// when it turns out to be broken (the host may have restarted its agent, for instance).
/// </summary>
public sealed class RemoteFileSource(IPeerConnection connection) : IFileSource, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;

    public async Task<DirectoryContents> ListAsync(string path, CancellationToken cancellationToken)
    {
        var response = await RequestAsync(new FileRequest { List = new ListRequest { Path = path } }, cancellationToken).ConfigureAwait(false);
        if (response.BodyCase != FileResponse.BodyOneofCase.Listing)
        {
            throw Unexpected(response);
        }

        var listing = response.Listing;
        Check(listing.Status);
        return new DirectoryContents(
            listing.Path,
            listing.Parent,
            [.. listing.Entries.Select(e => new FileEntryInfo(
                e.Name,
                e.IsDirectory,
                (long)e.Size,
                e.ModifiedUnixMs == 0 ? DateTimeOffset.MinValue : DateTimeOffset.FromUnixTimeMilliseconds(e.ModifiedUnixMs),
                (FileEntryAttributes)e.Attributes))],
            listing.Truncated);
    }

    public async Task<IReadOnlyList<PlaceInfo>> GetPlacesAsync(CancellationToken cancellationToken)
    {
        var response = await RequestAsync(new FileRequest { Places = new PlacesRequest() }, cancellationToken).ConfigureAwait(false);
        if (response.BodyCase != FileResponse.BodyOneofCase.Places)
        {
            throw Unexpected(response);
        }

        Check(response.Places.Status);
        return [.. response.Places.Places.Select(p => new PlaceInfo(p.Name, p.Path, p.IsDrive))];
    }

    public Task MakeDirectoryAsync(string path, CancellationToken cancellationToken) =>
        OperationAsync(new FileRequest { MakeDirectory = new MakeDirectoryRequest { Path = path } }, cancellationToken);

    public Task RenameAsync(string path, string newName, CancellationToken cancellationToken) =>
        OperationAsync(new FileRequest { Rename = new RenameRequest { Path = path, NewName = newName } }, cancellationToken);

    public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken) =>
        OperationAsync(new FileRequest { Delete = new DeleteRequest { Path = path, Recursive = recursive } }, cancellationToken);

    public string Combine(string folder, string name)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return name;
        }

        var separator = SeparatorOf(folder);
        return $"{folder.TrimEnd(separator)}{separator}{name}";
    }

    /// <summary>
    /// The separator the other computer writes paths with. It is the host's, not this machine's: a Posix controller
    /// driving a Windows host has to build <c>C:\dir\name</c> and a Windows controller driving a Posix host
    /// <c>/home/u/name</c>, and neither side is told what the other runs. The folder the host just handed over is the
    /// evidence — and it is reliable, because an absolute Windows path always starts with a drive letter or a UNC
    /// prefix, and nothing else does.
    /// </summary>
    private static char SeparatorOf(string folder) =>
        folder.Length >= 2 && folder[1] == ':' || folder.StartsWith(@"\\", StringComparison.Ordinal) ? '\\' : '/';

    public async ValueTask DisposeAsync()
    {
        await DropChannelAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task OperationAsync(FileRequest request, CancellationToken cancellationToken)
    {
        var response = await RequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.BodyCase != FileResponse.BodyOneofCase.Result)
        {
            throw Unexpected(response);
        }

        Check(response.Result.Status);
    }

    /// <remarks>
    /// Only a channel that worked before is retried on a fresh one: when a brand-new channel fails, asking again would
    /// not help, and repeating a folder operation that may already have happened would be wrong.
    /// </remarks>
    private async Task<FileResponse> RequestAsync(FileRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var reused = _channel is not null; ; reused = false)
            {
                try
                {
                    _channel ??= await connection.OpenChannelAsync(ChannelType.FileTransfer, bidirectional: true, cancellationToken).ConfigureAwait(false);
                    await ProtobufFrames.WriteAsync(_channel.Stream, request, cancellationToken).ConfigureAwait(false);
                    return await ProtobufFrames.ReadAsync(_channel.Stream, FileResponse.Parser, cancellationToken: cancellationToken).ConfigureAwait(false)
                        ?? throw new EndOfStreamException("The remote computer closed the file channel.");
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException && !cancellationToken.IsCancellationRequested)
                {
                    await DropChannelAsync().ConfigureAwait(false);
                    if (!reused)
                    {
                        throw new FileOperationException(FileError.Io, "The connection to the remote computer failed.", ex);
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DropChannelAsync()
    {
        var channel = Interlocked.Exchange(ref _channel, null);
        if (channel is not null)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void Check(FileStatus? status)
    {
        if (status is { Error: not FileError.None })
        {
            throw new FileOperationException(status.Error, string.IsNullOrEmpty(status.Message) ? status.Error.ToString() : status.Message);
        }
    }

    // A refusal of the whole request (e.g. the host cannot serve files right now) comes as a result instead of the expected answer.
    private static FileOperationException Unexpected(FileResponse response)
    {
        if (response.BodyCase == FileResponse.BodyOneofCase.Result)
        {
            Check(response.Result.Status);
        }

        return new FileOperationException(FileError.Unsupported, "The remote computer answered unexpectedly.");
    }
}
