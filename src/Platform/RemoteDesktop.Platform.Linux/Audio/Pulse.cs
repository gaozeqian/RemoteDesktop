using System.Runtime.InteropServices;

namespace RemoteDesktop.Platform.Linux.Audio;

/// <summary>
/// The parts of libpulse this platform layer uses.
/// </summary>
/// <remarks>
/// <para>
/// The main loop is driven from this side with <see cref="Iterate"/> rather than being handed callbacks. That is a
/// deliberate simplification: a callback-driven binding needs one managed entry point per event, each running on a
/// PulseAudio thread, each having to say what it came from. Polling the few states that matter is less machinery and
/// fewer places for a lifetime bug to hide.
/// </para>
/// <para>
/// The one callback that does exist, for subscription events, runs inside <see cref="Iterate"/> — on the caller's
/// thread — so it can set a field without any of this.
/// </para>
/// </remarks>
internal static unsafe partial class Pulse
{
    /// <summary>32-bit float, little-endian: what the pipeline carries and what Opus eats.</summary>
    internal const int SampleFormatFloat32Le = 5;

    // The two state enums are not the same enum and do not share their numbers. A context reaches READY at 4 and a
    // stream at 2, so a single set of constants makes one of them wait forever for a value it will never see — which
    // is exactly what happened here until a test pointed at it.
    internal const int ContextReady = 4;
    internal const int ContextFailed = 5;
    internal const int ContextTerminated = 6;

    internal const int StreamReady = 2;
    internal const int StreamFailed = 3;
    internal const int StreamTerminated = 4;

    internal const int StreamPlayback = 1;
    internal const int StreamRecord = 2;

    /// <summary>Neither a playback nor a record stream should be adjusted to match the device's own format.</summary>
    internal const int StreamFlagNoRemap = 0;

    /// <summary>The audio never leaves the machine.</summary>
    internal const int StreamFlagNoMove = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    /// <summary>A description of a stream, passed to the low-level stream constructors.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct BufferAttr
    {
        public uint MaxLength;
        public uint TargetLength;
        public uint PreBuffering;
        public uint MinimumRequest;
        public uint FragmentSize;
    }

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_mainloop_new")]
    internal static partial nint MainloopNew();

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_mainloop_free")]
    internal static partial void MainloopFree(nint mainloop);

    /// <summary>
    /// Processes whatever has arrived, without blocking. Returns a negative value on failure; the result of the
    /// iteration is written to <paramref name="returnValue"/>.
    /// </summary>
    [LibraryImport("libpulse.so.0", EntryPoint = "pa_mainloop_iterate")]
    internal static partial int Iterate(nint mainloop, int block, out int returnValue);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_mainloop_get_api")]
    internal static partial nint MainloopGetApi(nint mainloop);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_context_new", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint ContextNew(nint api, string name);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_context_unref")]
    internal static partial void ContextUnref(nint context);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_context_connect", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ContextConnect(nint context, string? server, int flags, nint spawnApi);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_context_disconnect")]
    internal static partial void ContextDisconnect(nint context);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_context_get_state")]
    internal static partial int ContextGetState(nint context);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_context_errno")]
    internal static partial int ContextErrno(nint context);

    /// <summary>
    /// Asks what a sink is called. The name "@DEFAULT_SINK@" is resolved by the server, so asking for it answers with
    /// whichever sink is the default right now — which is how a change of output is noticed without subscribing to
    /// events.
    /// </summary>
    [LibraryImport("libpulse.so.0", EntryPoint = "pa_context_get_sink_info_by_name", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint ContextGetSinkInfoByName(nint context, string name, nint callback, nint userdata);

    /// <summary>Every sink on the server. Used by a test that needs a second output to switch to.</summary>
    [LibraryImport("libpulse.so.0", EntryPoint = "pa_context_get_sink_info_list")]
    internal static partial nint ContextGetSinkInfoList(nint context, nint callback, nint userdata);

    /// <summary>
    /// Asks the server to load one of its own modules. Used by the tests, which need a second output device to
    /// switch to before they can check that a change of output is noticed.
    /// </summary>
    [LibraryImport("libpulse.so.0", EntryPoint = "pa_context_load_module", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint ContextLoadModule(nint context, string name, string argument, nint callback, nint userdata);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_context_unload_module")]
    internal static partial nint ContextUnloadModule(nint context, uint index, nint callback, nint userdata);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_context_set_default_sink", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint ContextSetDefaultSink(nint context, string name, nint callback, nint userdata);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_stream_new", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint StreamNew(nint context, string name, SampleSpec* spec, nint channelMap);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_stream_unref")]
    internal static partial void StreamUnref(nint stream);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_stream_connect_record", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int StreamConnectRecord(nint stream, string? device, BufferAttr* attributes, int flags);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_stream_connect_playback", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int StreamConnectPlayback(nint stream, string? device, BufferAttr* attributes, int flags, nint volume, nint syncStream);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_stream_disconnect")]
    internal static partial int StreamDisconnect(nint stream);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_stream_get_state")]
    internal static partial int StreamGetState(nint stream);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_stream_readable_size")]
    internal static partial nuint StreamReadableSize(nint stream);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_stream_peek")]
    internal static partial int StreamPeek(nint stream, out nint data, out nuint bytes);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_stream_drop")]
    internal static partial int StreamDrop(nint stream);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_stream_writable_size")]
    internal static partial nuint StreamWritableSize(nint stream);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_stream_write")]
    internal static partial int StreamWrite(nint stream, nint data, nuint bytes, nint freeCallback, long offset, int seekMode);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_stream_get_latency")]
    internal static partial int StreamGetLatency(nint stream, out long microseconds, out int negative);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_stream_flush")]
    internal static partial int StreamFlush(nint stream);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_stream_drain")]
    internal static partial nint StreamDrain(nint stream, nint callback, nint userdata);

    [LibraryImport("libpulse.so.0", EntryPoint = "pa_strerror")]
    internal static partial byte* StrError(int error);

    /// <summary>
    /// How many bytes one sample of a format takes.
    /// </summary>
    /// <remarks>
    /// Used to check the one constant in this file that could not be read out of a header: if
    /// <see cref="SampleFormatFloat32Le"/> were not what the server means by a 32-bit float, this answers something
    /// other than four. That turns a value which would otherwise be a guess into one the code verifies before it
    /// trusts it.
    /// </remarks>
    [LibraryImport("libpulse.so.0", EntryPoint = "pa_sample_size_of_format")]
    internal static partial nuint SampleSizeOfFormat(int format);

    /// <summary>The size in bytes the server says a sample spec needs.</summary>
    [LibraryImport("libpulse.so.0", EntryPoint = "pa_frame_size")]
    internal static partial nuint FrameSize(SampleSpec* spec);

    internal static string Describe(int error) =>
        Marshal.PtrToStringUTF8((nint)StrError(error)) ?? $"error {error}";
}
