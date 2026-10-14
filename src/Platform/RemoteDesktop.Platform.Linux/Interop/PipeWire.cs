using System.Runtime.InteropServices;

namespace RemoteDesktop.Platform.Linux.Interop;

/// <summary>
/// The part of PipeWire a screen capturer needs, bound by hand.
/// </summary>
/// <remarks>
/// <para>
/// The layouts here are the ones that matter and the ones that fail quietly. A field at the wrong offset does not
/// throw — it reads a number that looks like a width or a pointer that happens to be mapped — and the result is a
/// corrupted picture or a crash with nothing to connect it to. Every size and offset in this file was taken from a
/// program compiled against the real headers (<c>spa/buffer/buffer.h</c>, <c>spa/buffer/meta.h</c>,
/// <c>pipewire/stream.h</c>) rather than from documentation, and the sizes are asserted in the tests, so a struct
/// edited without checking shows up as a failing test rather than as a mystery.
/// </para>
/// <para>
/// The dev headers are not installed on a machine that only runs the product, which is why the numbers live here
/// rather than being generated at build time.
/// </para>
/// </remarks>
internal static unsafe partial class PipeWire
{
    internal const string Library = "libpipewire-0.3.so.0";

    // ---- Constants, read out of the headers by the compiler rather than counted by hand ----

    /// <summary>SPA_TYPE_OBJECT_Format — the object kind a negotiated format is.</summary>
    internal const uint SpaTypeObjectFormat = 262147;

    /// <summary>SPA_PARAM_EnumFormat — "here is what I would accept".</summary>
    internal const uint SpaParamEnumFormat = 3;

    /// <summary>SPA_PARAM_Format — "this is what was settled on".</summary>
    internal const uint SpaParamFormat = 4;

    /// <summary>SPA_PARAM_Meta.</summary>
    internal const uint SpaParamMeta = 6;

    /// <summary>SPA_FORMAT_VIDEO_format.</summary>
    internal const uint SpaFormatVideoFormat = 131073;

    /// <summary>SPA_FORMAT_VIDEO_size.</summary>
    internal const uint SpaFormatVideoSize = 131075;

    /// <summary>
    /// SPA_VIDEO_FORMAT_BGRx — four bytes a pixel, blue first, with the last byte padding.
    /// </summary>
    /// <remarks>
    /// Byte-for-byte the same as BGRA32, with the padding byte ignored. That is the point: the rest of the pipeline
    /// already works in BGRA, so the capture path needs no conversion at all. Asking for RGBA instead would mean
    /// swapping red and blue on a full-resolution frame, every frame, for nothing.
    /// </remarks>
    internal const uint SpaVideoFormatBgrx = 8;

    /// <summary>SPA_META_Cursor — where the pointer is, and what it looks like.</summary>
    internal const uint SpaMetaTypeCursor = 5;

    /// <summary>SPA_PARAM_META_type. A list of metadata kinds to be attached to the buffers.</summary>
    internal const uint SpaParamMetaType = 1;

    /// <summary>SPA_PARAM_META_size. How much room to leave for the metadata of each kind.</summary>
    internal const uint SpaParamMetaSize = 2;

    /// <summary>PW_DIRECTION_INPUT — this stream consumes video.</summary>
    internal const int DirectionInput = 0;

    /// <summary>PW_STREAM_FLAG_AUTOCONNECT — let the graph find the node rather than linking by hand.</summary>
    internal const uint StreamFlagAutoconnect = 1 << 0;

    /// <summary>PW_STREAM_FLAG_MAP_BUFFERS — mmap the buffers, so <c>data</c> is a usable pointer.</summary>
    internal const uint StreamFlagMapBuffers = 1 << 2;

    /// <summary>PW_STREAM_STATE_STREAMING — pictures are arriving.</summary>
    internal const int StreamStateStreaming = 3;

    /// <summary>PW_STREAM_STATE_ERROR.</summary>
    internal const int StreamStateError = -1;

    /// <summary>Enough room for a <c>spa_meta_cursor</c> plus a bitmap: a 64x64 cursor is 16 KB.</summary>
    internal const uint CursorMetaSize = 1024 * 1024;

    // ---- Structures ----

    /// <summary>A region of a buffer: <c>spa_chunk</c>, 16 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SpaChunk
    {
        /// <summary>Where the valid data starts, to be taken modulo the buffer's maxsize.</summary>
        public uint Offset;

        /// <summary>How many bytes are valid.</summary>
        public uint Size;

        /// <summary>Bytes per row, which is not <c>width * 4</c> when the producer padded it.</summary>
        public int Stride;

        /// <summary>SPA_CHUNK_FLAG_*, including <c>CORRUPTED</c> and <c>EMPTY</c>.</summary>
        public int Flags;
    }

    /// <summary>One plane of a buffer: <c>spa_data</c>, 40 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SpaData
    {
        public uint Type;
        public uint Flags;

        /// <summary>The descriptor, for memory that is not mapped — a dma-buf, for instance.</summary>
        public long Fd;

        public uint MapOffset;
        public uint MaxSize;

        /// <summary>Where the pixels are. Null unless the buffers were mapped.</summary>
        public nint Data;

        /// <summary>Points at a <see cref="SpaChunk"/> saying which part of <see cref="Data"/> is this frame.</summary>
        public nint Chunk;
    }

    /// <summary><c>spa_buffer</c>, 24 bytes: metadata, then one or more data planes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SpaBuffer
    {
        public uint MetaCount;
        public uint DataCount;
        public nint Metas;
        public nint Datas;
    }

    /// <summary><c>spa_meta</c>, 16 bytes: a tagged block hanging off a buffer.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SpaMeta
    {
        public uint Type;
        public uint Size;
        public nint Data;
    }

    /// <summary><c>spa_meta_cursor</c>, 28 bytes: the pointer, described separately from the picture.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SpaMetaCursor
    {
        /// <summary>Zero means no new cursor data in this frame.</summary>
        public uint Id;

        public uint Flags;

        /// <summary>Two <c>int32</c>: where the pointer is, in the captured screen's pixels.</summary>
        public int PositionX;
        public int PositionY;

        /// <summary>Two <c>int32</c>: which pixel of the bitmap is the point being pointed with.</summary>
        public int HotspotX;
        public int HotspotY;

        /// <summary>Offset of a <c>spa_meta_bitmap</c> from the start of this structure; 0 when unchanged.</summary>
        public uint BitmapOffset;
    }

    /// <summary><c>spa_meta_bitmap</c>, 20 bytes, sitting at <see cref="SpaMetaCursor.BitmapOffset"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SpaMetaBitmap
    {
        /// <summary>SPA_VIDEO_FORMAT_* of the bitmap, which for a cursor is BGRA.</summary>
        public uint Format;

        public uint Width;
        public uint Height;
        public int Stride;

        /// <summary>Where the pixels start, counted from the start of this structure.</summary>
        public uint Offset;
    }

    /// <summary><c>pw_buffer</c>, 40 bytes: PipeWire's wrapper around a <see cref="SpaBuffer"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PwBuffer
    {
        public nint Buffer;
        public nint UserData;
        public ulong Size;
        public ulong Requested;
        public ulong Time;
    }

    /// <summary>
    /// <c>pw_stream_events</c>, 96 bytes: the callbacks, as function pointers.
    /// </summary>
    /// <remarks>
    /// Pointers rather than delegates, because the struct outlives this call — PipeWire keeps the address — and a
    /// delegate marshalled here would be collected with nothing left pointing at it. The caller pins the delegates
    /// and fills these in. Order matters more here than anywhere else in the file: a field swapped with its
    /// neighbour turns <c>param_changed</c> into <c>add_buffer</c>, which is a call arriving with the wrong
    /// arguments and no way to tell.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PwStreamEvents
    {
        /// <summary>PW_VERSION_STREAM_EVENTS. PipeWire uses this to know how much of the struct to read.</summary>
        public uint Version;

        public nint Destroy;
        public nint StateChanged;
        public nint ControlInfo;
        public nint IoChanged;
        public nint ParamChanged;
        public nint AddBuffer;
        public nint RemoveBuffer;
        public nint Process;
        public nint Drained;
        public nint Command;
        public nint TriggerDone;
    }

    // ---- Entry points ----

    [LibraryImport(Library, EntryPoint = "pw_init")]
    internal static partial void Init(nint argc, nint argv);

    [LibraryImport(Library, EntryPoint = "pw_thread_loop_new")]
    internal static partial nint ThreadLoopNew([MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint properties);

    [LibraryImport(Library, EntryPoint = "pw_thread_loop_start")]
    internal static partial int ThreadLoopStart(nint loop);

    [LibraryImport(Library, EntryPoint = "pw_thread_loop_stop")]
    internal static partial void ThreadLoopStop(nint loop);

    [LibraryImport(Library, EntryPoint = "pw_thread_loop_destroy")]
    internal static partial void ThreadLoopDestroy(nint loop);

    [LibraryImport(Library, EntryPoint = "pw_thread_loop_lock")]
    internal static partial void ThreadLoopLock(nint loop);

    [LibraryImport(Library, EntryPoint = "pw_thread_loop_unlock")]
    internal static partial void ThreadLoopUnlock(nint loop);

    [LibraryImport(Library, EntryPoint = "pw_thread_loop_wait")]
    internal static partial void ThreadLoopWait(nint loop);

    [LibraryImport(Library, EntryPoint = "pw_thread_loop_signal")]
    internal static partial void ThreadLoopSignal(nint loop, int waitForAccept);

    [LibraryImport(Library, EntryPoint = "pw_thread_loop_get_loop")]
    internal static partial nint ThreadLoopGetLoop(nint loop);

    [LibraryImport(Library, EntryPoint = "pw_context_new")]
    internal static partial nint ContextNew(nint loop, nint properties, nuint userDataSize);

    [LibraryImport(Library, EntryPoint = "pw_context_destroy")]
    internal static partial void ContextDestroy(nint context);

    [LibraryImport(Library, EntryPoint = "pw_context_connect_fd", SetLastError = true)]
    internal static partial nint ContextConnectFd(nint context, int fd, nint properties, nuint userDataSize);

    [LibraryImport(Library, EntryPoint = "pw_stream_new")]
    internal static partial nint StreamNew(nint core, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint properties);

    [LibraryImport(Library, EntryPoint = "pw_stream_add_listener")]
    internal static partial void StreamAddListener(nint stream, nint listener, nint events, nint data);

    [LibraryImport(Library, EntryPoint = "pw_stream_connect")]
    internal static partial int StreamConnect(nint stream, int direction, uint targetId, uint flags, nint* parameters, uint parameterCount);

    [LibraryImport(Library, EntryPoint = "pw_stream_disconnect")]
    internal static partial int StreamDisconnect(nint stream);

    [LibraryImport(Library, EntryPoint = "pw_stream_destroy")]
    internal static partial void StreamDestroy(nint stream);

    [LibraryImport(Library, EntryPoint = "pw_stream_get_state")]
    internal static partial int StreamGetState(nint stream, nint* error);

    [LibraryImport(Library, EntryPoint = "pw_stream_dequeue_buffer")]
    internal static partial nint StreamDequeueBuffer(nint stream);

    [LibraryImport(Library, EntryPoint = "pw_stream_queue_buffer")]
    internal static partial int StreamQueueBuffer(nint stream, nint buffer);
}
