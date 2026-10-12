using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RemoteDesktop.Platform.MacOS.Display;

namespace RemoteDesktop.Platform.MacOS.Interop;

/// <summary>A rectangle in captured-frame pixels, as <c>rd_rect</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RdRect
{
    public int X;
    public int Y;
    public int Width;
    public int Height;
}

/// <summary>One captured frame, as <c>rd_frame</c>.</summary>
/// <remarks>
/// The pointers are only valid for the duration of the callback: they point into the CVPixelBuffer ScreenCaptureKit
/// lent the helper, and that buffer returns to its pool as soon as the callback returns. Every field has to be read
/// or copied inside the callback.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RdFrame
{
    public byte* Bgra;
    public int Width;
    public int Height;
    public int BytesPerRow;
    public ulong HostTimeNanoseconds;
    public RdRect* DirtyRects;
    public int DirtyCount;
}

/// <summary>The pointer, as <c>rd_cursor_snapshot</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RdCursorSnapshot
{
    /// <summary>Global display coordinates, top-left origin, in points — the space CGEvent positions use.</summary>
    public double X;

    public double Y;
    public int Visible;

    /// <summary>Non-zero when the shape differs from the previous read.</summary>
    public int Changed;

    /// <summary>Owned by the helper and valid until the next read; copy it before returning.</summary>
    public byte* Bgra;

    public int Width;
    public int Height;
    public int HotspotX;
    public int HotspotY;
}

/// <summary>One clipboard read, as <c>rd_clipboard_content</c>.</summary>
/// <remarks>
/// <see cref="Bytes"/> is owned by the helper and stays valid until the next clipboard read of any pasteboard, so
/// it has to be copied before another read is attempted.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RdClipboardContent
{
    /// <summary>One of <see cref="MacNativeInterop.ClipboardNone"/> and friends.</summary>
    public int Kind;

    public byte* Bytes;
    public nuint Length;
}

/// <summary>
/// The flat C ABI exported by <c>libRemoteDesktopMacNative.dylib</c>, built from <c>native/macos</c>.
/// </summary>
/// <remarks>
/// <para>
/// The Objective-C that ScreenCaptureKit and NSCursor require lives in the helper; this is the whole of the managed
/// side's view of it. The contract is in <c>native/macos/rd_native.h</c>, and it is deliberately narrow: opaque
/// handles, one struct per direction, and a function pointer for frame delivery. Nothing Objective-C crosses.
/// </para>
/// <para>
/// The static constructor installs <see cref="MacNativeLibraryResolver"/>. That is not decoration — without it the
/// first P/Invoke would depend on the runtime happening to find the dylib beside the assembly, and the failure
/// would be a bare <see cref="DllNotFoundException"/>. The runtime guarantees a static constructor has run before
/// any static member is reached, which is the ordering the resolver needs.
/// </para>
/// </remarks>
internal static unsafe partial class MacNativeInterop
{
    static MacNativeInterop() => MacNativeLibraryResolver.EnsureRegistered();

    // Status codes, mirroring the enum in rd_native.h.
    public const int Ok = 0;
    public const int ErrorArgument = -1;
    public const int ErrorPermission = -2;
    public const int ErrorDisplayNotFound = -3;
    public const int ErrorStream = -4;
    public const int ErrorTimeout = -5;
    public const int ErrorUnavailable = -6;

    /// <summary>Called on the stream's private serial queue, once per frame that has new content.</summary>
    public static delegate* unmanaged[Cdecl]<nint, RdFrame*, void> FrameCallback =>
        &OnFrame;

    /// <summary>The buffer size a caller should pass for <c>error</c>. The helper truncates rather than overruns.</summary>
    public const int ErrorBufferSize = 512;

    [LibraryImport(NativeLibraries.MacNative)]
    public static partial int rd_capture_create(
        uint displayId,
        int pixelWidth,
        int pixelHeight,
        int framesPerSecond,
        delegate* unmanaged[Cdecl]<nint, RdFrame*, void> callback,
        nint user,
        out nint capture,
        byte* error,
        nuint errorSize);

    [LibraryImport(NativeLibraries.MacNative)]
    public static partial int rd_capture_start(nint capture, byte* error, nuint errorSize);

    [LibraryImport(NativeLibraries.MacNative)]
    public static partial void rd_capture_stop(nint capture);

    [LibraryImport(NativeLibraries.MacNative)]
    public static partial void rd_capture_destroy(nint capture);

    [LibraryImport(NativeLibraries.MacNative)]
    public static partial int rd_cursor_read(RdCursorSnapshot* snapshot);

    // What a clipboard read produced, mirroring the enum in rd_native.h.
    public const int ClipboardNone = 0;
    public const int ClipboardText = 1;
    public const int ClipboardPng = 2;

    /// <summary>
    /// The pasteboard's change count, or -1 when it cannot be read. Only "different from last time" means anything.
    /// </summary>
    /// <param name="pasteboard">A pasteboard name, or <see langword="null"/> for the general pasteboard.</param>
    [LibraryImport(NativeLibraries.MacNative, StringMarshalling = StringMarshalling.Utf8)]
    public static partial long rd_clipboard_change_count(string? pasteboard);

    /// <param name="pasteboard">A pasteboard name, or <see langword="null"/> for the general pasteboard.</param>
    [LibraryImport(NativeLibraries.MacNative, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int rd_clipboard_read(string? pasteboard, RdClipboardContent* content);

    /// <remarks>
    /// The buffer and its length are two parameters here rather than a <c>ReadOnlySpan&lt;byte&gt;</c>, and that is
    /// not a style choice. Source-generated marshalling passes an array or a span as a <em>bare pointer</em>, with
    /// no length beside it — so a span in this position would leave the native <c>length</c> argument to be filled
    /// by whatever parameter came next, and the helper would then read that many bytes. It read the address of the
    /// caller's stack buffer as a length and died in <c>memmove</c> against a guard page, which is a memorable way
    /// to learn the rule.
    /// </remarks>
    /// <param name="pasteboard">A pasteboard name, or <see langword="null"/> for the general pasteboard.</param>
    [LibraryImport(NativeLibraries.MacNative, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int rd_clipboard_write_text(
        string? pasteboard, byte* utf8, nuint length, byte* error, nuint errorSize);

    /// <inheritdoc cref="rd_clipboard_write_text(string?, byte*, nuint, byte*, nuint)"/>
    [LibraryImport(NativeLibraries.MacNative, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int rd_clipboard_write_png(
        string? pasteboard, byte* png, nuint length, byte* error, nuint errorSize);

    [LibraryImport(NativeLibraries.MacNative, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int rd_open_settings(string anchor);

    /// <summary>Reads the helper's error buffer as a string, for surfacing the real reason.</summary>
    public static string ReadError(byte* error, int length)
    {
        var end = 0;
        while (end < length && error[end] != 0)
        {
            end++;
        }

        return end == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(error, end);
    }

    /// <summary>What a failed call produced, as a message that names the real cause.</summary>
    public static string DescribeStatus(int status) => status switch
    {
        ErrorArgument => "The native helper rejected an argument.",
        ErrorPermission => "Screen Recording has not been granted to this application.",
        ErrorDisplayNotFound => "The display is not shareable.",
        ErrorStream => "ScreenCaptureKit refused the stream.",
        ErrorTimeout => "The native helper timed out.",
        ErrorUnavailable => "The native helper cannot provide this on this system.",
        _ => $"The native helper failed with status {status}.",
    };

    // The address of this is handed to the helper, which calls it from its own thread. It must not throw: an
    // exception escaping into Objective-C would unwind through a frame that is not prepared for it, so the
    // implementation catches everything and reports failure through the state it already owns.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnFrame(nint user, RdFrame* frame)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is ScreenCaptureKitFrameSink sink)
            {
                sink.Deliver(frame);
            }
        }
        catch (Exception)
        {
            // Swallowed on purpose; see above. The capture reports the failure on the next managed call.
        }
    }
}
