using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Linux.Interop;
using RemoteDesktop.Platform.Posix.Interop;

namespace RemoteDesktop.Platform.Linux.Display;

/// <summary>
/// Captures one screen from a PipeWire stream the desktop portal handed over.
/// </summary>
/// <remarks>
/// <para>
/// The screen's pictures arrive on a thread PipeWire owns, and are handed out on whichever thread calls
/// <see cref="CaptureAsync"/>. Between the two is a one-frame queue: the callback copies into pooled memory and
/// returns immediately, because it is running in the graph and holding it up delays every other client on the
/// machine. Nothing that can block, allocate unboundedly, or throw into native code may happen there.
/// </para>
/// <para>
/// <b>Only one frame is kept.</b> A controller that is behind should see the current screen, not a backlog of the
/// last few seconds; when the queue is full the oldest frame is dropped and the new one takes its place. The
/// interface's "return null when nothing changed" then falls out of how PipeWire works — a compositor sends a buffer
/// when the screen is damaged, so no callback means no change, and <see cref="CaptureAsync"/> times out and says so.
/// </para>
/// <para>
/// The format is pinned to BGRx at the size the portal gave. BGRx is BGRA with a padding byte, so frames reach the
/// encoder without a colour conversion — the same pixels the Windows capture path produces.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class PipeWireScreenCapturer : IScreenCapturer
{
    /// <summary>One frame. Two would be a backlog on a link that is already behind.</summary>
    private const int QueueLength = 1;

    /// <summary>
    /// Room for a <c>spa_hook</c>, which is 48 bytes on PipeWire 1.6 and opaque. Over-allocating is free;
    /// under-allocating is memory corruption in a callback.
    /// </summary>
    private const int HookSize = 64;

    private readonly ILogger _logger;
    private readonly Channel<Captured> _frames;

    // The delegates must outlive this object's use of the stream: PipeWire keeps the function pointers, and a
    // collected delegate would be a call into freed memory. Holding them in fields is what keeps them alive.
    private readonly StateChangedCallback _stateChanged;
    private readonly ParamChangedCallback _paramChanged;
    private readonly ProcessCallback _process;

    private nint _loop;
    private nint _context;
    private nint _core;
    private nint _stream;
    private nint _hook;
    private nint _events;
    private nint _format;
    private GCHandle _self;
    private int _stride;
    private int _framesWithoutCursor;
    private int _disposed;

    /// <param name="fileDescriptor">
    /// The descriptor from the portal's <c>OpenPipeWireRemote</c>. It is the connection to the compositor's own
    /// PipeWire instance, which is why the stream cannot simply be created on the default one.
    /// </param>
    /// <param name="nodeId">Which of the granted screens to read, from the portal's stream list.</param>
    public PipeWireScreenCapturer(
        int fileDescriptor,
        uint nodeId,
        DisplayInfo display,
        int width,
        int height,
        ILogger<PipeWireScreenCapturer>? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fileDescriptor, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PipeWireScreenCapturer>.Instance;
        Display = display;
        _stride = width * 4;
        _frames = Channel.CreateBounded<Captured>(new BoundedChannelOptions(QueueLength)
        {
            // Not SingleReader: the process callback also drains the reader when it replaces a stale frame, so two
            // threads read the channel.
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropWrite,
        });

        _stateChanged = OnStateChanged;
        _paramChanged = OnParamChanged;
        _process = OnProcess;

        Open(fileDescriptor, nodeId, width, height);
    }

    public DisplayInfo Display { get; }

    public event EventHandler<CursorShape>? CursorChanged;

    public event EventHandler<CursorPosition>? CursorMoved;

    public async ValueTask<VideoFrame?> CaptureAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        // The timeout is a cancellation of this wait, not of the caller's token: the caller's cancellation means the
        // session is over, and this one only means the screen has not changed.
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(timeout);

        Captured captured;
        try
        {
            captured = await _frames.Reader.ReadAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }

        // Raised here rather than from the callback, because the interface promises both cursor events arrive from
        // inside this call, and because a controller handling them should see the pointer and the picture that belong
        // together. The shape is only announced when it has actually changed.
        if (captured.Cursor is { } cursor)
        {
            if (cursor.Shape is { } shape)
            {
                CursorChanged?.Invoke(this, shape);
            }

            CursorMoved?.Invoke(this, new CursorPosition(cursor.X, cursor.Y, cursor.Visible));
        }

        return captured.Frame;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _frames.Writer.TryComplete();
        DrainFrames();
        ReleaseNativeResources();

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Returns the pooled buffer of every frame still queued. Called after the channel is completed.</summary>
    private void DrainFrames()
    {
        while (_frames.Reader.TryRead(out var stale))
        {
            stale.Frame.Dispose();
        }
    }

    /// <summary>Releases every PipeWire and managed handle this capturer may have created, in dependency order.</summary>
    /// <remarks>Also called from <see cref="Open"/>'s failure path, where a partially-built capturer must not leak.</remarks>
    private unsafe void ReleaseNativeResources()
    {
        if (_stream != 0)
        {
            PipeWire.ThreadLoopLock(_loop);
            try
            {
                _ = PipeWire.StreamDisconnect(_stream);
                PipeWire.StreamDestroy(_stream);
                _stream = 0;
            }
            finally
            {
                PipeWire.ThreadLoopUnlock(_loop);
            }
        }

        if (_loop != 0)
        {
            PipeWire.ThreadLoopStop(_loop);
        }

        if (_core != 0)
        {
            PipeWire.ContextDestroy(_context);
            _context = 0;
            _core = 0;
        }

        if (_loop != 0)
        {
            PipeWire.ThreadLoopDestroy(_loop);
            _loop = 0;
        }

        if (_format != 0)
        {
            Marshal.FreeHGlobal(_format);
            _format = 0;
        }

        if (_hook != 0)
        {
            Marshal.FreeHGlobal(_hook);
            _hook = 0;
        }

        if (_events != 0)
        {
            Marshal.FreeHGlobal(_events);
            _events = 0;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    /// <summary>Starts the loop, then brings up the connection and the stream, releasing everything on failure.</summary>
    private unsafe void Open(int fileDescriptor, uint nodeId, int width, int height)
    {
        // Idempotent, and has to happen before anything else. The arguments are only used for logging.
        PipeWire.Init(0, 0);

        _loop = PipeWire.ThreadLoopNew("remotedesktop-capture", 0);
        if (_loop == 0)
        {
            throw new InvalidOperationException("PipeWire would not create a thread loop.");
        }

        _ = PipeWire.ThreadLoopStart(_loop);

        try
        {
            OpenCore(fileDescriptor, nodeId, width, height);
        }
        catch
        {
            ReleaseNativeResources();
            throw;
        }
    }

    /// <summary>Brings up the connection and the stream once the loop is running.</summary>
    private unsafe void OpenCore(int fileDescriptor, uint nodeId, int width, int height)
    {
        PipeWire.ThreadLoopLock(_loop);
        try
        {
            var loop = PipeWire.ThreadLoopGetLoop(_loop);
            _context = PipeWire.ContextNew(loop, 0, 0);
            if (_context == 0)
            {
                throw new InvalidOperationException("PipeWire would not create a context.");
            }

            // Connected to the descriptor the portal handed over, not to the default instance: that descriptor is the
            // compositor's own graph, which is the only one that has the screen in it.
            _core = PipeWire.ContextConnectFd(_context, fileDescriptor, 0, 0);
            if (_core == 0)
            {
                throw new InvalidOperationException(
                    $"PipeWire would not connect to the portal's descriptor (errno {Marshal.GetLastPInvokeError()}).");
            }

            // The node to read is named here rather than in the connect call. pw_stream_connect's target_id is
            // documented as "should have the value PW_ID_ANY", naming a target by id there is deprecated, and on
            // PipeWire 1.6 it is refused with -EINVAL and no explanation. The stream takes ownership of this.
            // Named by serial, and only reachable once the core has bound — see WaitForTarget.
            var target = WaitForTarget(nodeId);
            var properties = PipeWire.PropertiesNewString(
                $"target.object={target} media.type=Video media.category=Capture media.role=Screen");

            if (properties == 0)
            {
                throw new InvalidOperationException("PipeWire would not create the stream properties.");
            }

            _stream = PipeWire.StreamNew(_core, "remotedesktop-capture", properties);
            if (_stream == 0)
            {
                throw new InvalidOperationException("PipeWire would not create a capture stream.");
            }

            _self = GCHandle.Alloc(this);

            _hook = Marshal.AllocHGlobal(HookSize);
            Span<byte> hookMemory = new((void*)_hook, HookSize);
            hookMemory.Clear();

            _events = Marshal.AllocHGlobal(Marshal.SizeOf<PipeWire.PwStreamEvents>());
            var events = new PipeWire.PwStreamEvents
            {
                // PW_VERSION_STREAM_EVENTS. PipeWire reads only as far into the structure as this says.
                Version = 2,
                StateChanged = Marshal.GetFunctionPointerForDelegate(_stateChanged),
                ParamChanged = Marshal.GetFunctionPointerForDelegate(_paramChanged),
                Process = Marshal.GetFunctionPointerForDelegate(_process),
            };

            Marshal.StructureToPtr(events, _events, fDeleteOld: false);
            PipeWire.StreamAddListener(_stream, _hook, _events, GCHandle.ToIntPtr(_self));

            Connect(nodeId, width, height);
        }
        finally
        {
            PipeWire.ThreadLoopUnlock(_loop);
        }
    }

    /// <summary>Starts the stream, then tells it what format is wanted.</summary>
    /// <remarks>
    /// Two calls, in two steps, and that is the part worth explaining. Passing these very same pods to
    /// <c>pw_stream_connect</c> comes back as <c>-EINVAL</c> — the stream is left unconnected, the library logs
    /// nothing about why, and the pods themselves are byte-for-byte what <c>spa_pod_builder_add_object</c> produces
    /// for the same request. Handing them to <c>pw_stream_update_params</c> immediately afterwards is accepted. The
    /// two were compared against a running compositor rather than reasoned about, and the conclusion is only that
    /// this is the shape this PipeWire accepts: connect with nothing, then describe the format.
    /// </remarks>
    private unsafe void Connect(uint nodeId, int width, int height)
    {

        // Autoconnect lets the graph attach the stream to the node named in its properties. Mapping the buffers is
        // what makes the pixels reachable through a pointer instead of through a descriptor we would have to map.
        var connected = PipeWire.StreamConnect(
            _stream,
            PipeWire.DirectionInput,
            PipeWire.IdAny,
            PipeWire.StreamFlagAutoconnect | PipeWire.StreamFlagMapBuffers,
            0,
            0);

        if (connected < 0)
        {
            throw new InvalidOperationException(
                $"PipeWire refused the capture stream (error {connected}): {DescribeState(_stream)}");
        }

        byte[][] pods =
        [
            SpaPodBuilder.EnumFormatBgrx(width, height),
            SpaPodBuilder.CursorMeta((int)PipeWire.CursorMetaSize),
        ];

        var buffers = new nint[pods.Length];
        var parameters = (nint*)Marshal.AllocHGlobal(pods.Length * sizeof(nint));

        try
        {
            for (var i = 0; i < pods.Length; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(pods[i].Length);
                Marshal.Copy(pods[i], 0, buffers[i], pods[i].Length);
                parameters[i] = buffers[i];
            }

            var updated = PipeWire.StreamUpdateParams(_stream, (nint)parameters, (uint)pods.Length);
            if (updated < 0)
            {
                throw new InvalidOperationException(
                    $"PipeWire refused the capture format (error {updated}): {DescribeState(_stream)}");
            }
        }
        finally
        {
            foreach (var buffer in buffers)
            {
                if (buffer != 0)
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            Marshal.FreeHGlobal((nint)parameters);
        }
    }

    private void OnStateChanged(nint data, int oldState, int state, nint error)
    {
        _ = data;
        _ = oldState;

        if (state == PipeWire.StreamStateError)
        {
            var message = error == 0 ? "no reason given" : Marshal.PtrToStringUTF8(error) ?? "unreadable reason";
            LogStreamError(_logger, message);
            _frames.Writer.TryComplete();
            DrainFrames();
        }
        else
        {
            LogStateChanged(_logger, state);
        }
    }

    /// <summary>
    /// Learns what was actually agreed to, which is not always what was asked for.
    /// </summary>
    /// <remarks>
    /// The compositor answers with the format it settled on, and the stride in it is the one that matters — a
    /// producer may pad rows, and a frame copied out with the assumed <c>width * 4</c> would be skewed a little more
    /// on every row, which reads as a rendering fault rather than as arithmetic.
    /// </remarks>
    private void OnParamChanged(nint data, uint id, nint param)
    {
        _ = data;

        if (id != PipeWire.SpaParamFormat || param == 0)
        {
            return;
        }

        try
        {
            var pod = SpaPod.Parse(ReadPod(param));
            foreach (var property in pod.AsProperties())
            {
                if (property.Key != PipeWire.SpaFormatVideoFormat && property.Key != PipeWire.SpaFormatVideoSize)
                {
                    continue;
                }

                LogNegotiated(_logger, property.Key, (uint)property.Value.Type);
            }
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        {
            // A format we could not read is not fatal on its own: the frames either arrive in a shape we can handle
            // or they do not, and the process callback is where that shows up.
            LogFormatUnreadable(_logger, error.Message);
        }
    }

    /// <summary>
    /// Takes a frame. Runs on PipeWire's thread, so it copies and returns.
    /// </summary>
    private unsafe void OnProcess(nint data)
    {
        _ = data;

        var raw = PipeWire.StreamDequeueBuffer(_stream);
        if (raw == 0)
        {
            return;
        }

        try
        {
            var buffer = Marshal.PtrToStructure<PipeWire.PwBuffer>(raw);
            if (buffer.Buffer == 0)
            {
                return;
            }

            var spaBuffer = Marshal.PtrToStructure<PipeWire.SpaBuffer>(buffer.Buffer);
            if (spaBuffer.DataCount == 0 || spaBuffer.Datas == 0)
            {
                return;
            }

            var plane = Marshal.PtrToStructure<PipeWire.SpaData>(spaBuffer.Datas);
            if (plane.Data == 0 || plane.Chunk == 0)
            {
                return;
            }

            var chunk = Marshal.PtrToStructure<PipeWire.SpaChunk>(plane.Chunk);
            if (chunk.Size == 0)
            {
                return;
            }

            var cursor = ReadCursor(spaBuffer);
            if (cursor is null)
            {
                // The compositor is not obliged to describe the pointer, and the answer is to say so once rather than
                // to ask again every frame. A few frames are allowed first so that a slow start does not trip it.
                if (Interlocked.Increment(ref _framesWithoutCursor) == 10)
                {
                    LogNoCursorMetadata(_logger);
                }
            }
            else
            {
                Interlocked.Exchange(ref _framesWithoutCursor, 0);
            }
            var frame = Copy(plane.Data, chunk);
            if (frame is null)
            {
                return;
            }

            // The queue holds one frame. A frame that is still queued is stale — the newest picture wins — and its
            // pooled buffer must be returned before it is dropped, or it leaks.
            while (_frames.Reader.TryRead(out var stale))
            {
                stale.Frame.Dispose();
            }

            if (!_frames.Writer.TryWrite(new Captured(frame, cursor)))
            {
                // The channel was completed (stream error or disposal), so the frame can never be delivered.
                frame.Dispose();
            }
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        {
            LogFrameUnreadable(_logger, error.Message);
        }
        finally
        {
            // Always given back, or the stream runs out of buffers and the screen freezes.
            _ = PipeWire.StreamQueueBuffer(_stream, raw);
        }
    }

    /// <summary>Copies one frame out of mapped memory into a pooled frame the caller owns.</summary>
    private unsafe MemoryVideoFrame? Copy(nint pixels, in PipeWire.SpaChunk chunk)
    {
        var stride = chunk.Stride > 0 ? chunk.Stride : _stride;
        var height = Display.Bounds.Height;
        var width = Display.Bounds.Width;

        if (stride < width * 4)
        {
            LogStrideTooSmall(_logger, stride, width * 4);
            return null;
        }

        var frame = new MemoryVideoFrame(width, height, PixelFormat.Bgra32, TimeSpan.FromMilliseconds(Environment.TickCount64));
        var destination = frame.Buffer;

        // Row by row, because the producer's stride is its own business and is often wider than the picture. A
        // straight block copy would shift every row a little further across than the last.
        var available = Math.Min(chunk.Size, (uint)(stride * height));
        var rows = (int)(available / (uint)stride);

        for (var y = 0; y < rows && y < height; y++)
        {
            var source = pixels + (nint)chunk.Offset + (y * stride);
            var row = new ReadOnlySpan<byte>((void*)source, width * 4);
            row.CopyTo(destination[(y * frame.Stride)..]);
        }

        return frame;
    }

    /// <summary>
    /// Reads the pointer out of the buffer's metadata, if the compositor attached any.
    /// </summary>
    /// <remarks>
    /// Metadata the compositor is not obliged to send. When it is absent the pointer is simply not described, and
    /// the controller draws whatever it last heard about — better than a pointer that jumps to the corner every
    /// frame because a missing position was read as zero.
    /// </remarks>
    private static unsafe CursorUpdate? ReadCursor(in PipeWire.SpaBuffer buffer)
    {
        for (var i = 0; i < buffer.MetaCount; i++)
        {
            var meta = Marshal.PtrToStructure<PipeWire.SpaMeta>(buffer.Metas + (i * Marshal.SizeOf<PipeWire.SpaMeta>()));
            if (meta.Type != PipeWire.SpaMetaTypeCursor || meta.Data == 0)
            {
                continue;
            }

            var cursor = Marshal.PtrToStructure<PipeWire.SpaMetaCursor>(meta.Data);
            if (cursor.Id == 0)
            {
                // No new cursor data in this frame. The position still counts, so it is used and no shape is
                // announced.
                return new CursorUpdate(cursor.PositionX, cursor.PositionY, Visible: true, Shape: null, Id: 0);
            }

            CursorShape? shape = null;
            if (cursor.BitmapOffset != 0)
            {
                shape = ReadBitmap(meta.Data, cursor);
            }

            return new CursorUpdate(cursor.PositionX, cursor.PositionY, Visible: true, shape, cursor.Id);
        }

        return null;
    }

    /// <summary>
    /// Turns the cursor's bitmap into straight-alpha BGRA.
    /// </summary>
    /// <remarks>
    /// The bitmap arrives premultiplied, which is what a compositor wants and what nothing downstream does: a
    /// renderer drawing it normally would get an edge that shades towards black. Undoing it here costs one pass over
    /// a few kilobytes on the frames where the pointer changes — which is a handful of times a session.
    /// </remarks>
    private static unsafe CursorShape? ReadBitmap(nint meta, in PipeWire.SpaMetaCursor cursor)
    {
        var bitmap = Marshal.PtrToStructure<PipeWire.SpaMetaBitmap>(meta + (nint)cursor.BitmapOffset);
        if (bitmap.Width is 0 or > 512 || bitmap.Height is 0 or > 512)
        {
            return null;
        }

        var stride = bitmap.Stride > 0 ? bitmap.Stride : (int)bitmap.Width * 4;
        if (stride < bitmap.Width * 4)
        {
            return null;
        }

        var source = (byte*)(meta + (nint)cursor.BitmapOffset + (nint)bitmap.Offset);
        var bgra = new byte[bitmap.Width * bitmap.Height * 4];

        for (var y = 0; y < bitmap.Height; y++)
        {
            var row = new ReadOnlySpan<byte>(source + (y * stride), (int)bitmap.Width * 4);
            row.CopyTo(bgra.AsSpan((int)(y * bitmap.Width * 4)));

            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = (int)((y * bitmap.Width * 4) + (x * 4));
                var alpha = bgra[pixel + 3];
                if (alpha is 0 or 255)
                {
                    // Fully transparent stays transparent; fully opaque was never premultiplied.
                    continue;
                }

                bgra[pixel] = (byte)Math.Min(255, bgra[pixel] * 255 / alpha);
                bgra[pixel + 1] = (byte)Math.Min(255, bgra[pixel + 1] * 255 / alpha);
                bgra[pixel + 2] = (byte)Math.Min(255, bgra[pixel + 2] * 255 / alpha);
            }
        }

        return new CursorShape((int)bitmap.Width, (int)bitmap.Height, cursor.HotspotX, cursor.HotspotY, bgra);
    }

    /// <summary>
    /// Waits for the screen the portal granted to appear, and returns the serial to name it by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>pw_context_connect_fd</c> returns as soon as the descriptor is taken; the core is not yet bound and the
    /// list of objects it can see is still empty. Connecting the stream before that list arrives is what produced
    /// <c>no target node available</c> — the node was there a moment later, the look-up had simply happened too
    /// early. Waiting for the node itself, rather than for a fixed delay, is both correct and quick.
    /// </para>
    /// <para>
    /// What the portal calls a node id and what the stream wants are not the same number. On this machine the
    /// granted node has id 78 and <c>object.serial</c> 1199. The serial is what the stream is named by, because it
    /// is the one that stays meaningful across the graph being rebuilt.
    /// </para>
    /// </remarks>
    private unsafe uint WaitForTarget(uint nodeId)
    {
        var registry = PipeWire.CoreGetRegistry(_core, PipeWire.RegistryVersion, 0);
        if (registry == 0)
        {
            throw new InvalidOperationException("PipeWire would not give up its registry.");
        }

        using var found = new ManualResetEventSlim(false);
        var serial = nodeId;

        TargetLookup lookup = (_, id, _, _, _, props) =>
        {
            if (id != nodeId)
            {
                return;
            }

            if (TryGetProperty(props, "object.serial", out var text) && uint.TryParse(text, out var parsed) && parsed != 0)
            {
                serial = parsed;
            }

            found.Set();
        };

        // Rooted for as long as PipeWire holds the function pointer, which is the whole of this method.
        var handle = GCHandle.Alloc(lookup);
        var hook = Marshal.AllocHGlobal(HookSize);
        new Span<byte>((void*)hook, HookSize).Clear();
        var events = Marshal.AllocHGlobal(Marshal.SizeOf<PipeWire.PwRegistryEvents>());
        Marshal.StructureToPtr(
            new PipeWire.PwRegistryEvents { Version = 0, Global = Marshal.GetFunctionPointerForDelegate(lookup) },
            events,
            fDeleteOld: false);

        PipeWire.RegistryAddListener(registry, hook, events, 0);

        // The loop has to be given back while waiting. Events are delivered on the loop's own thread, so a caller
        // holding the lock waits for something that cannot happen — which is exactly how the first attempt at
        // reading this registry managed to see nothing on a connection that had a node on it.
        PipeWire.ThreadLoopUnlock(_loop);
        var arrived = found.Wait(TimeSpan.FromSeconds(5));
        PipeWire.ThreadLoopLock(_loop);

        PipeWire.RegistryDestroy(registry);
        Marshal.FreeHGlobal(events);
        Marshal.FreeHGlobal(hook);
        handle.Free();

        if (!arrived)
        {
            throw new InvalidOperationException(
                $"The screen the desktop granted (node {nodeId}) never appeared on the PipeWire connection.");
        }

        return serial;
    }

    /// <summary>One property out of an <c>spa_dict</c>.</summary>
    private static unsafe bool TryGetProperty(nint dict, string key, out string value)
    {
        value = string.Empty;
        if (dict == 0)
        {
            return false;
        }

        var header = Marshal.PtrToStructure<PipeWire.SpaDict>(dict);
        var size = Marshal.SizeOf<PipeWire.SpaDictItem>();

        for (var i = 0; i < header.ItemCount; i++)
        {
            var item = Marshal.PtrToStructure<PipeWire.SpaDictItem>(header.Items + (i * size));
            if (!string.Equals(Marshal.PtrToStringUTF8(item.Key), key, StringComparison.Ordinal))
            {
                continue;
            }

            value = Marshal.PtrToStringUTF8(item.Value) ?? string.Empty;
            return true;
        }

        return false;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TargetLookup(nint data, uint id, uint permissions, nint type, uint version, nint props);

    /// <summary>Asks the stream why it is unhappy, which it answers in words.</summary>
    private static unsafe string DescribeState(nint stream)
    {
        var error = (byte*)0;
        var state = PipeWire.StreamGetState(stream, &error);
        var reason = error == null ? "no reason given" : Marshal.PtrToStringUTF8((nint)error) ?? "unreadable reason";
        return $"state {state}: {reason}";
    }

    private static unsafe byte[] ReadPod(nint param)
    {
        // {uint32 size; uint32 type} then the body, which is what the size word counts.
        var header = new ReadOnlySpan<byte>((void*)param, SpaPod.HeaderSize);
        var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header);
        var bytes = new byte[SpaPod.HeaderSize + (int)size];
        Marshal.Copy(param, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>One frame on its way to the caller, with whatever the pointer was doing at the time.</summary>
    private readonly record struct Captured(MemoryVideoFrame Frame, CursorUpdate? Cursor);

    private readonly record struct CursorUpdate(int X, int Y, bool Visible, CursorShape? Shape, uint Id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StateChangedCallback(nint data, int oldState, int state, nint error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ParamChangedCallback(nint data, uint id, nint param);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ProcessCallback(nint data);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The capture stream is now in state {State}.")]
    private static partial void LogStateChanged(ILogger logger, int state);

    [LoggerMessage(Level = LogLevel.Error, Message = "The capture stream failed: {Reason}")]
    private static partial void LogStreamError(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The compositor settled {Key} with a pod of type {Type}.")]
    private static partial void LogNegotiated(ILogger logger, uint key, uint type);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The negotiated format could not be read: {Reason}")]
    private static partial void LogFormatUnreadable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A captured frame could not be read: {Reason}")]
    private static partial void LogFrameUnreadable(ILogger logger, string reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The compositor is not describing the pointer, so the controller will draw whatever it last heard about.")]
    private static partial void LogNoCursorMetadata(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The compositor sent rows of {Stride} bytes for a picture that needs {Needed}; the frame was dropped.")]
    private static partial void LogStrideTooSmall(ILogger logger, int stride, int needed);
}
