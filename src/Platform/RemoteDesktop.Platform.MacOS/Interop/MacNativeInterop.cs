using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RemoteDesktop.Platform.MacOS.Audio;
using RemoteDesktop.Platform.MacOS.Camera;
using RemoteDesktop.Platform.MacOS.Display;
using RemoteDesktop.Platform.MacOS.Video;

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

/// <summary>One camera, as <c>rd_camera_info</c>.</summary>
/// <remarks>
/// Both strings belong to the helper and stay valid only until the next listing call, so the enumerator copies them
/// into managed strings before asking for anything else.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RdCameraInfo
{
    public byte* Id;
    public byte* Name;
}

/// <summary>One picture size and rate a camera offers, as <c>rd_camera_mode</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RdCameraMode
{
    public int Width;
    public int Height;
    public double FrameRate;
}

/// <summary>One camera picture, as <c>rd_camera_frame</c>.</summary>
/// <remarks>
/// As with <see cref="RdFrame"/>, the pixels are only valid for the duration of the callback.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RdCameraFrame
{
    public byte* Bgra;
    public int Width;
    public int Height;
    public int BytesPerRow;
    public ulong HostTimeNanoseconds;
}

/// <summary>One chunk of captured audio, as <c>rd_audio_chunk</c>.</summary>
/// <remarks>
/// As with <see cref="RdFrame"/>, the samples are only valid for the duration of the callback: the helper reuses
/// one buffer for every chunk, so the consumer has to copy before returning.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RdAudioChunk
{
    /// <summary><see cref="FrameCount"/> times <see cref="ChannelCount"/> floats, channel-interleaved.</summary>
    public float* Samples;

    public int FrameCount;
    public int ChannelCount;
    public int SampleRate;
}

/// <summary>One access unit the encoder produced, as <c>rd_video_packet</c>.</summary>
/// <remarks>
/// <see cref="Data"/> belongs to the encoder and stays valid only until the next call to
/// <see cref="MacNativeInterop.rd_video_encoder_encode"/>, so the managed side copies it before calling again.
/// Dropping one of these desynchronises the decoder's reference pictures, which is why the encoder hands back a
/// list rather than a single frame.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RdVideoPacket
{
    public byte* Data;
    public nuint Length;
    public int IsKeyFrame;
}

/// <summary>One decoded picture, as <c>rd_video_picture</c>.</summary>
/// <remarks>
/// The two planes are tightly packed by the helper; it copied them out of VideoToolbox's buffer precisely so they
/// could be. Valid only until the next decode call.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RdVideoPicture
{
    public byte* Y;
    public byte* Uv;
    public int YStride;
    public int UvStride;
    public int Width;

    /// <summary>
    /// The picture's height. Declared even though the caller usually knows it, because leaving it out shifts every
    /// field after it — <see cref="HasFrame"/> would then be read from this one's slot.
    /// </summary>
    public int Height;

    public int HasFrame;
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
    public const int ErrorDeviceNotFound = -7;
    public const int ErrorAudioDevice = -8;

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

    // Camera authorization, mirroring the enum in rd_native.h.
    public const int CameraNotDetermined = 0;
    public const int CameraRestricted = 1;
    public const int CameraDenied = 2;
    public const int CameraAuthorized = 3;

    [LibraryImport(NativeLibraries.MacNative)]
    public static partial int rd_camera_authorization_status();

    [LibraryImport(NativeLibraries.MacNative)]
    public static partial int rd_camera_request_access();

    /// <remarks>
    /// <c>out</c> is an array owned by the helper. Its strings stay valid until the next listing call, so the
    /// enumerator reads them into managed strings immediately and never holds the pointer.
    /// </remarks>
    [LibraryImport(NativeLibraries.MacNative)]
    public static partial int rd_camera_list(
        out RdCameraInfo* cameras, out int count, byte* error, nuint errorSize);

    [LibraryImport(NativeLibraries.MacNative, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int rd_camera_modes(
        string cameraId, out RdCameraMode* modes, out int count, byte* error, nuint errorSize);

    /// <summary>Called on the session's private serial queue, once per picture.</summary>
    public static delegate* unmanaged[Cdecl]<nint, RdCameraFrame*, void> CameraFrameCallback => &OnCameraFrame;

    [LibraryImport(NativeLibraries.MacNative, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int rd_camera_create(
        string cameraId,
        int width,
        int height,
        double frameRate,
        delegate* unmanaged[Cdecl]<nint, RdCameraFrame*, void> callback,
        nint user,
        out nint camera,
        byte* error,
        nuint errorSize);

    [LibraryImport(NativeLibraries.MacNative)]
    public static partial int rd_camera_start(nint camera, byte* error, nuint errorSize);

    [LibraryImport(NativeLibraries.MacNative)]
    public static partial void rd_camera_stop(nint camera);

    [LibraryImport(NativeLibraries.MacNative)]
    public static partial void rd_camera_destroy(nint camera);

    /// <summary>Called on the audio stream's private serial queue, once per chunk. Serialized, so no locking here.</summary>
    public static delegate* unmanaged[Cdecl]<nint, RdAudioChunk*, void> AudioChunkCallback => &OnAudioChunk;

    /// <summary>
    /// Starts loopback capture of what the machine is playing.
    /// </summary>
    /// <remarks>
    /// This is the system mix, not a microphone: it needs the Screen Recording grant and no microphone grant, and it
    /// is what makes <see cref="RemoteDesktop.Abstractions.Audio.IAudioCapturer"/>'s "what the host is playing"
    /// contract true on this platform. Audio from this process is excluded natively, so the far end's own voice does
    /// not come back around.
    /// </remarks>
    [LibraryImport(NativeLibraries.MacNative)]
    public static partial int rd_audio_capture_create(
        delegate* unmanaged[Cdecl]<nint, RdAudioChunk*, void> callback,
        nint user,
        out nint capture,
        byte* error,
        nuint errorSize);

    [LibraryImport(NativeLibraries.MacNative)]
    public static partial int rd_audio_capture_start(nint capture, byte* error, nuint errorSize);

    [LibraryImport(NativeLibraries.MacNative)]
    public static partial void rd_audio_capture_stop(nint capture);

    [LibraryImport(NativeLibraries.MacNative)]
    public static partial void rd_audio_capture_destroy(nint capture);

    [LibraryImport(NativeLibraries.MacNative)]
    public static partial int rd_audio_player_create(
        int sampleRate, int channelCount, out nint player, byte* error, nuint errorSize);

    [LibraryImport(NativeLibraries.MacNative)]
    public static partial int rd_audio_player_start(nint player, byte* error, nuint errorSize);

    /// <remarks>
    /// A pointer and a length rather than a <c>ReadOnlySpan&lt;float&gt;</c>, for the reason recorded on
    /// <see cref="rd_clipboard_write_text(string?, byte*, nuint, byte*, nuint)"/>: the source-generated marshaller
    /// passes a span as a bare pointer with no length beside it, so the native <c>sample_count</c> would be filled
    /// by whatever argument followed.
    /// </remarks>
    [LibraryImport(NativeLibraries.MacNative)]
    public static partial int rd_audio_player_enqueue(
        nint player, float* samples, nuint sampleCount, byte* error, nuint errorSize);

    /// <summary>Samples queued but not yet played, or -1 for an invalid handle.</summary>
    [LibraryImport(NativeLibraries.MacNative)]
    public static partial long rd_audio_player_buffered(nint player);

    [LibraryImport(NativeLibraries.MacNative)]
    public static partial void rd_audio_player_stop(nint player);

    /// <summary>Empties the queue without stopping the device.</summary>
    [LibraryImport(NativeLibraries.MacNative)]
    public static partial void rd_audio_player_discard(nint player);

    [LibraryImport(NativeLibraries.MacNative)]
    public static partial void rd_audio_player_destroy(nint player);

    #region Video

    /// <summary>Called when a compressed picture became available.</summary>
    public static delegate* unmanaged[Cdecl]<nint, void> VideoEncoderCallback => &OnVideoEncoded;

    /// <summary>Called when a decoded picture became available.</summary>
    public static delegate* unmanaged[Cdecl]<nint, void> VideoDecoderCallback => &OnVideoDecoded;

    /// <remarks>
    /// The buffer and its length are declared separately for the reason recorded on
    /// <see cref="rd_clipboard_write_text(string?, byte*, nuint, byte*, nuint)"/>.
    /// </remarks>
    [LibraryImport(NativeLibraries.MacNative)]
    public static partial int rd_video_encoder_create(
        int codec,
        int width,
        int height,
        int frameRate,
        int bitrateKbps,
        int preferSoftware,
        delegate* unmanaged[Cdecl]<nint, void> callback,
        nint user,
        out nint encoder,
        byte* name,
        nuint nameSize,
        out int isHardware,
        byte* error,
        nuint errorSize);

    [LibraryImport(NativeLibraries.MacNative)]
    public static partial int rd_video_encoder_set_bitrate(nint encoder, int bitrateKbps);

    [LibraryImport(NativeLibraries.MacNative)]
    public static partial int rd_video_encoder_encode(
        nint encoder,
        byte* nv12,
        nuint nv12Size,
        int forceKeyFrame,
        long timestampNanoseconds,
        RdVideoPacket* packets,
        int capacity,
        out int count,
        int timeoutMilliseconds,
        byte* error,
        nuint errorSize);

    [LibraryImport(NativeLibraries.MacNative)]
    public static partial void rd_video_encoder_destroy(nint encoder);

    [LibraryImport(NativeLibraries.MacNative)]
    public static partial int rd_video_decoder_create(
        int codec,
        delegate* unmanaged[Cdecl]<nint, void> callback,
        nint user,
        out nint decoder,
        byte* error,
        nuint errorSize);

    [LibraryImport(NativeLibraries.MacNative)]
    public static partial int rd_video_decoder_decode(
        nint decoder,
        byte* annexB,
        nuint annexBSize,
        long timestampNanoseconds,
        int timeoutMilliseconds,
        RdVideoPicture* picture,
        byte* error,
        nuint errorSize);

    [LibraryImport(NativeLibraries.MacNative)]
    public static partial void rd_video_decoder_destroy(nint decoder);

    #endregion

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
        ErrorDeviceNotFound => "That device is not connected.",
        ErrorAudioDevice => "No audio device could be opened.",
        _ => $"The native helper failed with status {status}.",
    };

    /// <summary>
    /// The helper's own message when it wrote one, falling back to the generic text for the status.
    /// </summary>
    /// <remarks>
    /// The helper's message is always more specific — "Camera access has not been granted to this application"
    /// against "Screen Recording has not been granted" — and it is the one that names the device or the permission
    /// actually at fault, so it is preferred whenever it is present.
    /// </remarks>
    public static unsafe string Describe(int status, byte* error, int errorSize)
    {
        var detail = ReadError(error, errorSize);
        return detail.Length > 0 ? detail : DescribeStatus(status);
    }

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

    /// <inheritdoc cref="OnFrame"/>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnCameraFrame(nint user, RdCameraFrame* frame)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is MacCameraFrameSink sink)
            {
                sink.Deliver(frame);
            }
        }
        catch (Exception)
        {
            // Swallowed on purpose; see OnFrame.
        }
    }

    /// <inheritdoc cref="OnFrame"/>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnAudioChunk(nint user, RdAudioChunk* chunk)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is MacAudioChunkSink sink)
            {
                sink.Deliver(chunk);
            }
        }
        catch (Exception)
        {
            // Swallowed on purpose; see OnFrame.
        }
    }

    // The two video callbacks have no work of their own — the helper queues what it produced and the next
    // encode/decode call drains it — so what they check is that the sink is still there. Reaching the target
    // through the handle is that check: if the managed side disposed the sink, the native session was destroyed
    // first, so this cannot happen, and any exception is swallowed rather than unwinding into the helper.
    private static void ConfirmVideoSink(nint user)
    {
        try
        {
            _ = GCHandle.FromIntPtr(user).Target;
        }
        catch (Exception)
        {
            // See OnFrame.
        }
    }

    /// <inheritdoc cref="OnFrame"/>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnVideoEncoded(nint user) => ConfirmVideoSink(user);

    /// <inheritdoc cref="OnFrame"/>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnVideoDecoded(nint user) => ConfirmVideoSink(user);
}
