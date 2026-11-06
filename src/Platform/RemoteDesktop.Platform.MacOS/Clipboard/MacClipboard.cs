using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Clipboard;
using RemoteDesktop.Platform.MacOS.Interop;

namespace RemoteDesktop.Platform.MacOS.Clipboard;

/// <summary>
/// The macOS pasteboard as text or a PNG picture, through the Objective-C helper.
/// </summary>
/// <remarks>
/// <para>
/// Changes are noticed by polling the pasteboard's change count, a few times a second and only while somebody is
/// listening. That is the same shape the Windows implementation has, and it is forced by the platform rather than
/// chosen: NSPasteboard posts no notification when another process takes the clipboard over.
/// </para>
/// <para>
/// Polling the <em>count</em> rather than the content is deliberate. Reading the content on a timer would pull
/// whatever the user copied — including a picture they never meant to share — into this process several times a
/// second, and on macOS 15 and later it would also make the system show its "pasted from" notice over and over.
/// The content is read once, after something actually changed.
/// </para>
/// <para>
/// There is no permission to check: unlike Screen Recording, the general pasteboard is not behind TCC.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed partial class MacClipboard : IClipboard, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// A ceiling on what a single read will pull into managed memory.
    /// </summary>
    /// <remarks>
    /// This is a guard against a pathological pasteboard, not policy: what is worth sharing is decided by
    /// <c>ClipboardSynchronizer</c>, which enforces the real limits and drops anything larger. This only stops a
    /// stray 200 MB picture from being materialised in full on every change.
    /// </remarks>
    private const long MaxContentBytes = 32L * 1024 * 1024;

    /// <summary>
    /// Serializes reads against the helper's single read buffer.
    /// </summary>
    /// <remarks>
    /// Static because the resource it protects is: <c>rd_clipboard_read</c> hands back a pointer into one buffer
    /// owned by the helper, valid only until the next read of <em>any</em> pasteboard. Two instances reading
    /// different pasteboards would still be writing to the same memory, so an instance-level lock would not be
    /// enough. Reads are rare — one per actual change — so the contention costs nothing.
    /// </remarks>
    private static readonly Lock ReadGate = new();

    private readonly string? _pasteboard;
    private readonly ILogger<MacClipboard> _logger;
    private readonly Lock _lock = new();
    private Timer? _poll;
    private long _lastChangeCount;
    private int _listeners;
    private EventHandler? _changed;
    private bool _disposed;

    /// <param name="pasteboard">
    /// The pasteboard to use, or <see langword="null"/> for the general one. Naming it exists for the tests, which
    /// work on a private pasteboard rather than overwriting whatever the user had copied.
    /// </param>
    public MacClipboard(string? pasteboard = null, ILogger<MacClipboard>? logger = null)
    {
        _pasteboard = pasteboard;
        _logger = logger ?? NullLogger<MacClipboard>.Instance;
    }

    public event EventHandler? Changed
    {
        add
        {
            lock (_lock)
            {
                _changed += value;
                if (_listeners++ == 0)
                {
                    // Snapshot on the first listener so a change that happened before anyone was listening is not
                    // reported — only changes travel, never whatever was already on the clipboard.
                    _lastChangeCount = MacNativeInterop.rd_clipboard_change_count(_pasteboard);
                    _poll = new Timer(_ => Poll(), null, PollInterval, PollInterval);
                }
            }
        }

        remove
        {
            lock (_lock)
            {
                _changed -= value;
                if (--_listeners == 0)
                {
                    _poll?.Dispose();
                    _poll = null;
                }
            }
        }
    }

    /// <returns><see langword="null"/> when the pasteboard is empty or holds nothing this layer represents.</returns>
    /// <exception cref="IOException">The pasteboard could not be read.</exception>
    public ClipboardContent? Get()
    {
        lock (ReadGate)
        {
            unsafe
            {
                var content = default(RdClipboardContent);
                var status = MacNativeInterop.rd_clipboard_read(_pasteboard, &content);
                if (status != MacNativeInterop.Ok)
                {
                    throw new IOException(MacNativeInterop.DescribeStatus(status));
                }

                // NONE is the ordinary answer for an empty pasteboard, and the helper reports it as success.
                if (content.Kind == MacNativeInterop.ClipboardNone || content.Bytes is null || content.Length == 0)
                {
                    return null;
                }

                if (content.Length > MaxContentBytes)
                {
                    return null;
                }

                // Copied here, inside the gate: the pointer stops being valid the moment the next read happens.
                var bytes = new ReadOnlySpan<byte>(content.Bytes, (int)content.Length).ToArray();
                return content.Kind switch
                {
                    MacNativeInterop.ClipboardText => new TextClipboardContent(Encoding.UTF8.GetString(bytes)),
                    MacNativeInterop.ClipboardPng => new ImageClipboardContent(bytes),
                    _ => null,
                };
            }
        }
    }

    /// <exception cref="ArgumentException">The content is not a kind this platform can put on the pasteboard.</exception>
    /// <exception cref="IOException">The pasteboard refused the write.</exception>
    public void Set(ClipboardContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        unsafe
        {
            var error = stackalloc byte[MacNativeInterop.ErrorBufferSize];
            error[0] = 0;
            var errorSize = (nuint)MacNativeInterop.ErrorBufferSize;

            int status;
            switch (content)
            {
                // Both cases pin explicitly and pass the length themselves. The marshaller will not do it: it turns
                // an array or a span into a bare pointer and nothing else, so a length left to it would silently
                // become whatever argument came next. See the note on the interop declarations.
                case TextClipboardContent text:
                    var utf8 = Encoding.UTF8.GetBytes(text.Text);
                    fixed (byte* bytes = utf8)
                    {
                        status = MacNativeInterop.rd_clipboard_write_text(
                            _pasteboard, bytes, (nuint)utf8.Length, error, errorSize);
                    }

                    break;

                case ImageClipboardContent image:
                    fixed (byte* bytes = image.Png.Span)
                    {
                        status = MacNativeInterop.rd_clipboard_write_png(
                            _pasteboard, bytes, (nuint)image.Png.Length, error, errorSize);
                    }

                    break;

                default:
                    throw new ArgumentException($"Unsupported clipboard content {content.GetType().Name}.", nameof(content));
            }

            if (status != MacNativeInterop.Ok)
            {
                var detail = MacNativeInterop.ReadError(error, MacNativeInterop.ErrorBufferSize);
                throw new IOException(detail.Length > 0 ? detail : MacNativeInterop.DescribeStatus(status));
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _poll?.Dispose();
            _poll = null;
        }
    }

    private void Poll()
    {
        EventHandler? handler;
        lock (_lock)
        {
            var count = MacNativeInterop.rd_clipboard_change_count(_pasteboard);

            // A negative count means the pasteboard could not be read at all. Treating that as "unchanged" keeps a
            // transient failure from turning into a stream of change events.
            if (count < 0 || count == _lastChangeCount)
            {
                return;
            }

            _lastChangeCount = count;
            handler = _changed;
        }

        try
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // A timer callback is a thread-pool entry point: an exception escaping here ends the process. The next
            // pasteboard change is still picked up, so dropping this one is the safer failure.
            LogHandlerFailed(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A clipboard change handler threw; that change was not reported.")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception);
}
