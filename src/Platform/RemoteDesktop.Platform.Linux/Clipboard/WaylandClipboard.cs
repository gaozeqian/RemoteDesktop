using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using RemoteDesktop.Abstractions.Clipboard;
using RemoteDesktop.Platform.Linux.XdgPortal;

namespace RemoteDesktop.Platform.Linux.Clipboard;

/// <summary>
/// The clipboard on Wayland, by way of the desktop portal.
/// </summary>
/// <remarks>
/// <para>
/// A process that is not a Wayland client cannot reach the clipboard: the selection belongs to the compositor and is
/// handed out over a Wayland connection. The portal is the way round that, and it attaches the clipboard to a
/// <em>remote-desktop</em> session — never to a screen-cast one, which is refused with "Invalid session type".
/// </para>
/// <para>
/// Everything about this class follows from the portal's model rather than from a clipboard's. Contents are not
/// stored anywhere: the portal says who owns the selection and in which formats, and the bytes move over pipes on
/// demand — <see cref="Get"/> reads them, and a transfer the other side asks for is answered by writing into a pipe
/// the portal opened. So there is no local clipboard to keep in step with; there is a conversation about one.
/// </para>
/// <para>
/// <b>Only changes are reported.</b> The interface's <c>Changed</c> means "the clipboard changed and it was not us",
/// which is what the synchroniser listens for. Our own <c>Set</c> deliberately does not raise it: the portal reports
/// it back as an owner change, and passing that on would have each side send the other its own copy back.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class WaylandClipboard(
    PortalControlSession control,
    ILogger<WaylandClipboard> logger) : IClipboard, IDisposable
{
    /// <summary>The formats offered to the other side, most specific first.</summary>
    private static readonly string[] TextTypes = ["text/plain;charset=utf-8", "text/plain", "UTF8_STRING", "STRING"];
    private static readonly string[] ImageTypes = ["image/png"];

    /// <summary>How long to wait for the other side to write a transfer before giving up on it.</summary>
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The largest selection read or written in one go; the same ceiling the synchroniser enforces.</summary>
    private const int MaxBytes = 8 * 1024 * 1024;

    private readonly Lock _gate = new();

    private ClipboardContent? _owned;
    private IReadOnlyList<string> _available = [];
    private string? _watchedSession;
    private bool _isOwner;

    public event EventHandler? Changed;

    /// <inheritdoc />
    public ClipboardContent? Get()
    {
        if (!control.EnsureReady())
        {
            return null;
        }

        var (connection, session) = control.Current();
        Watch(connection, session);

        lock (_gate)
        {
            // Our own copy is the authoritative one while we own the selection: asking the portal for content it
            // would have to come back to us for is a round trip through a pipe to reach a field.
            if (_isOwner)
            {
                return _owned;
            }

            // What the portal says is on offer, when it has said. It does not always: the owner-change signal is
            // not sent for every transition, and it was not sent at all on the machine this was written on. So the
            // formats are tried in order of preference rather than only when advertised — asking for one nobody has
            // costs an error the portal answers immediately, which is cheaper than never reading anything.
            //
            // Text first, because it is the common case and a fraction of the size. An image is only read when there
            // is no text at all, which is what a screenshot looks like.
            var text = Pick(_available, TextTypes) ?? (_available.Count == 0 ? TextTypes[0] : null);
            if (text is not null)
            {
                return ReadText(connection, session, text);
            }

            var image = Pick(_available, ImageTypes) ?? (_available.Count == 0 ? ImageTypes[0] : null);
            return image is null ? null : ReadImage(connection, session, image);
        }
    }

    /// <inheritdoc />
    public void Set(ClipboardContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!control.EnsureReady())
        {
            LogUnavailable(logger, content.GetType().Name);
            return;
        }

        var (connection, session) = control.Current();
        Watch(connection, session);

        lock (_gate)
        {
            _owned = content;
            _isOwner = true;
        }

        // Advertised with the format it is in and nothing else. Offering both and then failing to answer one of them
        // is worse than offering the one that will be there.
        var types = content is ImageClipboardContent ? ImageTypes : TextTypes;

        try
        {
            connection.SetSelectionAsync(session, types, CancellationToken.None).GetAwaiter().GetResult();

            // Described into a local first: the analyzer refuses a method call in the argument position, on the
            // grounds that it would be evaluated whether or not anything is listening.
            var what = Describe(content);
            LogPublished(logger, types[0], what);
        }
        catch (Exception error) when (error is PortalException or InvalidOperationException)
        {
            LogRefused(logger, error.Message);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _owned = null;
            _available = [];
        }
    }

    /// <summary>
    /// Subscribes to the session's clipboard signals, once per session.
    /// </summary>
    /// <remarks>
    /// Per session because a session can end — the desktop can revoke control, or be restarted — and the next one is
    /// a different object path whose signals the old subscription will never see.
    /// </remarks>
    private void Watch(PortalConnection connection, string session)
    {
        lock (_gate)
        {
            if (_watchedSession == session)
            {
                return;
            }

            _watchedSession = session;
            _isOwner = false;
            _available = [];
        }

        // Not under the lock: this awaits, and the callbacks it installs take the lock themselves.
        connection.WatchClipboardAsync(
            session,
            onOwnerChanged: (types, isOwner) =>
            {
                lock (_gate)
                {
                    _available = types;
                    _isOwner = isOwner;
                }

                // Ours does not count as a change, or each side would send the other its own copy back.
                if (!isOwner)
                {
                    LogOwnerChanged(logger, types.Count);
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            },
            onTransfer: (mimeType, serial) => Answer(connection, session, mimeType, serial),
            CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Writes our content into the pipe the portal opened for a peer that asked for it.</summary>
    private void Answer(PortalConnection connection, string session, string mimeType, uint serial)
    {
        var payload = mimeType.StartsWith("image/", StringComparison.Ordinal)
            ? (Copy() as ImageClipboardContent)?.Png
            : System.Text.Encoding.UTF8.GetBytes((Copy() as TextClipboardContent)?.Text ?? string.Empty);

        if (payload is null)
        {
            // Asked for a format we do not have. Saying so lets the other side move on instead of waiting on a pipe
            // nobody is going to write to.
            _ = connection.SelectionWriteDoneAsync(session, serial, success: false).ConfigureAwait(false);
            return;
        }

        try
        {
            using var pipe = connection.SelectionWriteAsync(session, serial, CancellationToken.None).GetAwaiter().GetResult();

            // The portal reports success or failure for us, so the write is given a deadline: a reader that has gone
            // away would otherwise leave this thread parked forever holding the session.
            using var writer = new FileStream(pipe, FileAccess.Write);
            writer.WriteTimeout = (int)TransferTimeout.TotalMilliseconds;
            writer.Write(payload.Value.Span);
            writer.Flush();

            _ = connection.SelectionWriteDoneAsync(session, serial, success: true).ConfigureAwait(false);
            LogAnswered(logger, mimeType, payload.Value.Length);
        }
        catch (Exception error) when (error is IOException or PortalException or InvalidOperationException)
        {
            LogTransferFailed(logger, error.Message);
        }
    }

    private ClipboardContent? Copy()
    {
        lock (_gate)
        {
            return _owned;
        }
    }

    private TextClipboardContent? ReadText(PortalConnection connection, string session, string mimeType)
    {
        var bytes = Read(connection, session, mimeType);
        return bytes is null ? null : new TextClipboardContent(System.Text.Encoding.UTF8.GetString(bytes));
    }

    private ImageClipboardContent? ReadImage(PortalConnection connection, string session, string mimeType)
    {
        var bytes = Read(connection, session, mimeType);
        return bytes is null ? null : new ImageClipboardContent(bytes);
    }

    /// <summary>Reads the selection in one format, or nothing if the other side will not supply it.</summary>
    private byte[]? Read(PortalConnection connection, string session, string mimeType)
    {
        SafeFileHandle? pipe = null;
        try
        {
            pipe = connection.SelectionReadAsync(session, mimeType, CancellationToken.None).GetAwaiter().GetResult();

            // On a worker with a deadline rather than inline. The descriptor is the reading end of a pipe that
            // another program writes to, and one that never writes leaves a read parked for ever — which on the
            // thread that asked for the clipboard is a window that has stopped responding. Closing the descriptor
            // is what unblocks it, so that is what giving up does.
            var reading = Task.Run(() => Drain(pipe), CancellationToken.None);
            if (!reading.Wait(TransferTimeout))
            {
                pipe.Dispose();
                pipe = null;
                LogReadTimeout(logger, mimeType);
                return null;
            }

            return reading.Result;
        }
        catch (Exception error) when (error is IOException or PortalException or InvalidOperationException or AggregateException)
        {
            LogReadFailed(logger, mimeType, error.Message);
            return null;
        }
        finally
        {
            pipe?.Dispose();
        }
    }

    /// <summary>Reads a selection to the end, or gives up on it when it grows past what will be accepted.</summary>
    private static byte[]? Drain(SafeFileHandle pipe)
    {
        try
        {
            using var reader = new FileStream(pipe, FileAccess.Read);
            var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];

            // Bounded, because the other end is another program: a selection that claims to be enormous must not be
            // able to exhaust this one's memory. What is over the limit is dropped rather than truncated — half a
            // picture is not a picture.
            int read;
            while ((read = reader.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>The first of <paramref name="wanted"/> that the other side actually offers.</summary>
    private static string? Pick(IReadOnlyList<string> available, string[] wanted)
    {
        foreach (var candidate in wanted)
        {
            foreach (var offered in available)
            {
                if (string.Equals(offered, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return offered;
                }
            }
        }

        return null;
    }

    private static string Describe(ClipboardContent content) => content switch
    {
        TextClipboardContent text => $"{text.Text.Length} characters",
        ImageClipboardContent image => $"{image.Png.Length} bytes of PNG",
        _ => content.GetType().Name,
    };

    [LoggerMessage(Level = LogLevel.Debug, Message = "The clipboard has changed; {Count} format(s) on offer.")]
    private static partial void LogOwnerChanged(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Published the clipboard as {Format} ({What}).")]
    private static partial void LogPublished(ILogger logger, string format, string what);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Answered a request for {Format} with {Bytes} bytes.")]
    private static partial void LogAnswered(ILogger logger, string format, int bytes);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not read the clipboard as {Format}: {Reason}")]
    private static partial void LogReadFailed(ILogger logger, string format, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Nothing wrote {Format} to the clipboard within the deadline, so it was not read.")]
    private static partial void LogReadTimeout(ILogger logger, string format);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The clipboard holds more than {Limit} bytes of {Format}, so it was not read.")]
    private static partial void LogTooLarge(ILogger logger, string format, int limit);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The clipboard could not be published: {Reason}")]
    private static partial void LogRefused(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A clipboard transfer could not be answered: {Reason}")]
    private static partial void LogTransferFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The clipboard is not available, so {Kind} was not copied.")]
    private static partial void LogUnavailable(ILogger logger, string kind);
}
