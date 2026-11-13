using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Media;

namespace RemoteDesktop.Platform.Windows.Display;

/// <summary>
/// GDI capture of one display, used where Desktop Duplication cannot work: an RDP session, or a machine whose DXGI
/// adapter cannot duplicate its output. It copies the screen with <c>BitBlt</c> and paints the cursor into the image,
/// because GDI has no cursor channel of its own.
/// </summary>
/// <remarks>
/// This is the compatibility path, not the fast one: every capture is a CPU copy, so it is paced to a rate that is
/// useful for remote control without saturating the machine. Desktop Duplication remains the default on a local
/// session.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class GdiScreenCapturer : IScreenCapturer
{
    private const uint SourceCopy = 0x00CC0020;
    private const uint CaptureLayered = 0x40000000;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;
    private const uint CursorShowing = 0x00000001;
    private const uint DrawNormal = 0x00000003;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;

    // 20 fps: enough for remote control, and a fifth of the copies a 60 Hz capture loop would make.
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(50);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DisplayInfo _display;
    private readonly nint _screenDc;
    private readonly nint _memoryDc;
    private readonly nint _bitmap;
    private readonly nint _previousBitmap;
    private readonly nint _bits;
    private readonly int _width;
    private readonly int _height;
    private readonly int _sourceX;
    private readonly int _sourceY;
    private bool _disposed;
    private bool _faulted;

    public GdiScreenCapturer(DisplayInfo display)
    {
        ArgumentNullException.ThrowIfNull(display);

        _display = display;
        _width = display.Bounds.Width;
        _height = display.Bounds.Height;
        if (_width <= 0 || _height <= 0)
        {
            throw new ArgumentException("The display has no area.", nameof(display));
        }

        _screenDc = GetDC(0);
        if (_screenDc == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open the screen device context for {display.Id}.");
        }

        try
        {
            _memoryDc = CreateCompatibleDC(_screenDc);
            if (_memoryDc == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = _width,
                    // Negative: a top-down DIB, whose first row is the top of the screen.
                    Height = -_height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb,
                },
            };

            _bitmap = CreateDIBSection(_screenDc, ref info, DibRgbColors, out _bits, 0, 0);
            if (_bitmap == 0 || _bits == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            _previousBitmap = SelectObject(_memoryDc, _bitmap);
            if (_previousBitmap == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            _sourceX = display.Bounds.X - GetSystemMetrics(SmXVirtualScreen);
            _sourceY = display.Bounds.Y - GetSystemMetrics(SmYVirtualScreen);
        }
        catch
        {
            Release();
            throw;
        }
    }

    public DisplayInfo Display => _display;

    /// <summary>True after a BitBlt that failed; cleared by the next successful capture.</summary>
    public bool IsFaulted => _faulted;

    public event EventHandler<CursorShape>? CursorChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<CursorPosition>? CursorMoved
    {
        add { }
        remove { }
    }

    public ValueTask<VideoFrame?> CaptureAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
        {
            return ValueTask.FromResult<VideoFrame?>(null);
        }

        if (!BitBlt(_memoryDc, 0, 0, _width, _height, _screenDc, _sourceX, _sourceY, SourceCopy | CaptureLayered)
            && !BitBlt(_memoryDc, 0, 0, _width, _height, _screenDc, _sourceX, _sourceY, SourceCopy))
        {
            _faulted = true;
            Thread.Sleep(timeout);
            return ValueTask.FromResult<VideoFrame?>(null);
        }

        _faulted = false;
        DrawCursor();

        var frame = new MemoryVideoFrame(_width, _height, PixelFormat.Bgra32, _clock.Elapsed);
        unsafe
        {
            fixed (byte* destination = frame.Buffer)
            {
                Buffer.MemoryCopy((void*)_bits, destination, frame.Length, frame.Length);
            }
        }

        // The caller asks continuously; pace the CPU copy rather than spinning on the desktop.
        Thread.Sleep(FrameInterval);
        return ValueTask.FromResult<VideoFrame?>(frame);
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            Release();
        }

        return ValueTask.CompletedTask;
    }

    private void DrawCursor()
    {
        var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref cursor) || (cursor.Flags & CursorShowing) == 0 || cursor.Cursor == 0)
        {
            return;
        }

        if (!GetIconInfo(cursor.Cursor, out var icon))
        {
            return;
        }

        try
        {
            var x = cursor.ScreenPosition.X - icon.HotspotX - GetSystemMetrics(SmXVirtualScreen);
            var y = cursor.ScreenPosition.Y - icon.HotspotY - GetSystemMetrics(SmYVirtualScreen);
            _ = DrawIconEx(_memoryDc, x, y, cursor.Cursor, 0, 0, 0, 0, DrawNormal);
        }
        finally
        {
            if (icon.MaskBitmap != 0)
            {
                _ = DeleteObject(icon.MaskBitmap);
            }

            if (icon.ColorBitmap != 0)
            {
                _ = DeleteObject(icon.ColorBitmap);
            }
        }
    }

    private void Release()
    {
        if (_memoryDc != 0)
        {
            if (_previousBitmap != 0)
            {
                _ = SelectObject(_memoryDc, _previousBitmap);
            }

            _ = DeleteDC(_memoryDc);
        }

        if (_bitmap != 0)
        {
            _ = DeleteObject(_bitmap);
        }

        if (_screenDc != 0)
        {
            _ = ReleaseDC(0, _screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public uint Flags;
        public nint Cursor;
        public Point ScreenPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public int HotspotX;
        public int HotspotY;
        public nint MaskBitmap;
        public nint ColorBitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorInfo(ref CursorInfo info);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetIconInfo(nint icon, out IconInfo info);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint SelectObject(nint dc, nint obj);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);
}
