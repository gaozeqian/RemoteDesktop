using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Clipboard;

namespace RemoteDesktop.Platform.Windows.Clipboard;

/// <summary>
/// The Windows clipboard as text or a PNG picture. Changes are noticed by polling the clipboard sequence number (a few
/// times a second, only while somebody listens), which needs no window of its own for listening.
/// </summary>
/// <remarks>
/// Writing needs a window to own the data, or <c>SetClipboardData</c> fails. A hidden message-only window lives on a
/// thread that pumps messages: other programs send it messages when they take the clipboard over, and an idle thread
/// would make them wait.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsClipboard : IClipboard, IDisposable
{
    private const uint CfUnicodeText = 13;
    private const uint CfDib = 8;
    private const uint GmemMoveable = 0x0002;
    private const int OpenAttempts = 10;
    private const nint HwndMessage = -3;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly uint _pngFormat = RegisterClipboardFormat("PNG");
    private readonly ILogger<WindowsClipboard> _logger;
    private readonly Lock _lock = new();
    private readonly ManualResetEventSlim _windowReady = new();
    private Thread? _windowThread;
    private uint _windowThreadId;
    private nint _window;
    private Timer? _poll;
    private uint _lastSequence;
    private int _listeners;
    private EventHandler? _changed;
    private bool _disposed;

    public WindowsClipboard(ILogger<WindowsClipboard>? logger = null) =>
        _logger = logger ?? NullLogger<WindowsClipboard>.Instance;

    public event EventHandler? Changed
    {
        add
        {
            lock (_lock)
            {
                _changed += value;
                if (_listeners++ == 0)
                {
                    _lastSequence = GetClipboardSequenceNumber();
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

    /// <summary>Text wins over a picture when both are present (copying spreadsheet cells puts both there).</summary>
    public ClipboardContent? Get()
    {
        if (!TryOpen(0))
        {
            return null;
        }

        try
        {
            if (IsClipboardFormatAvailable(CfUnicodeText) && ReadGlobal(GetClipboardData(CfUnicodeText)) is { } textBytes)
            {
                var text = Encoding.Unicode.GetString(textBytes);
                var end = text.IndexOf('\0', StringComparison.Ordinal);
                text = end >= 0 ? text[..end] : text;
                return text.Length == 0 ? null : new TextClipboardContent(text);
            }

            if (IsClipboardFormatAvailable(_pngFormat) && ReadGlobal(GetClipboardData(_pngFormat)) is { } png)
            {
                return new ImageClipboardContent(png);
            }

            if (IsClipboardFormatAvailable(CfDib) && ReadGlobal(GetClipboardData(CfDib)) is { } dib && DibConverter.ToPng(dib) is { } converted)
            {
                return new ImageClipboardContent(converted);
            }

            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <exception cref="IOException">The clipboard stayed locked by another program.</exception>
    public void Set(ClipboardContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var (format, bytes) = content switch
        {
            TextClipboardContent text => (CfUnicodeText, Encoding.Unicode.GetBytes(text.Text + '\0')),
            ImageClipboardContent image => (_pngFormat, image.Png.ToArray()),
            _ => throw new ArgumentException($"Unsupported clipboard content {content.GetType().Name}.", nameof(content)),
        };

        if (!TryOpen(EnsureWindow()))
        {
            throw new IOException("The clipboard is in use by another program.");
        }

        try
        {
            EmptyClipboard();
            var memory = GlobalAlloc(GmemMoveable, (nuint)bytes.Length);
            if (memory == 0)
            {
                throw new IOException("Could not allocate clipboard memory.");
            }

            var target = GlobalLock(memory);
            if (target == 0)
            {
                GlobalFree(memory);
                throw new IOException("Could not lock clipboard memory.");
            }

            Marshal.Copy(bytes, 0, target, bytes.Length);
            GlobalUnlock(memory);

            // On success the system owns the memory.
            if (SetClipboardData(format, memory) == 0)
            {
                GlobalFree(memory);
                throw new IOException("The clipboard refused the data.");
            }
        }
        finally
        {
            CloseClipboard();
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

        if (_windowThread is not null)
        {
            PostThreadMessage(_windowThreadId, WmQuit, 0, 0);
            _windowThread.Join(TimeSpan.FromSeconds(2));
        }

        _windowReady.Dispose();
    }

    private void Poll()
    {
        EventHandler? handler;
        lock (_lock)
        {
            var sequence = GetClipboardSequenceNumber();
            if (sequence == _lastSequence)
            {
                return;
            }

            _lastSequence = sequence;
            handler = _changed;
        }

        try
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // A timer callback is a thread-pool entry point: an exception escaping here ends the process. The next
            // clipboard change is still picked up, so dropping this one is the safer failure.
            LogHandlerFailed(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A clipboard change handler threw; that change was not reported.")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception);

    private nint EnsureWindow()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_windowThread is null)
            {
                _windowThread = new Thread(WindowThread) { IsBackground = true, Name = "Clipboard owner window" };
                _windowThread.SetApartmentState(ApartmentState.STA);
                _windowThread.Start();
            }
        }

        _windowReady.Wait(TimeSpan.FromSeconds(5));
        return _window != 0 ? _window : throw new IOException("Could not create the clipboard owner window.");
    }

    private void WindowThread()
    {
        _windowThreadId = GetCurrentThreadId();
        _window = CreateWindowEx(0, "STATIC", null, 0, 0, 0, 0, 0, HwndMessage, 0, 0, 0);
        _windowReady.Set();
        if (_window == 0)
        {
            return;
        }

        while (GetMessage(out var message, 0, 0, 0) > 0)
        {
            DispatchMessage(in message);
        }

        DestroyWindow(_window);
    }

    private static bool TryOpen(nint owner)
    {
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (OpenClipboard(owner))
            {
                return true;
            }

            Thread.Sleep(15);
        }

        return false;
    }

    private static byte[]? ReadGlobal(nint handle)
    {
        if (handle == 0)
        {
            return null;
        }

        var size = (int)Math.Min(GlobalSize(handle), int.MaxValue);
        var pointer = GlobalLock(handle);
        if (pointer == 0 || size <= 0)
        {
            return null;
        }

        try
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, size);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private const uint WmQuit = 0x0012;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetClipboardData(uint format, nint memory);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormat(string name);

    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial nuint GlobalSize(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(
        uint exStyle, string className, string? windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out Message message, nint window, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(in Message message);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }
}
