using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Linux.Display;
using RemoteDesktop.Platform.Linux.Interop;
using RemoteDesktop.Platform.Linux.Video;

namespace RemoteDesktop.Platform.Linux.Camera;

/// <summary>
/// One camera, read from the PipeWire node the desktop portal granted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not yet run against a camera.</b> Every line below is patterned on <see cref="PipeWireScreenCapturer"/>,
/// which is verified against a real compositor, and the pieces that are not plumbing — the format requests, the
/// JPEG decode, the 4:2:2 rearrangement — have tests and real byte-level fixtures. What has never happened is the
/// whole thing running: this machine reports no camera and has no <c>/dev/video*</c>. Treat it as a starting point
/// that needs a machine with a camera, not as a feature that has been shown to work.
/// </para>
/// <para>
/// The camera is a node in the desktop's own PipeWire graph, reached through the descriptor the portal handed over,
/// and it is read exactly the way a captured screen is: ask for a format, take buffers as they arrive. What differs
/// is what comes in them — a webcam offers JPEG, or raw 4:2:2, and neither is the BGRx a compositor sends — so
/// frames are turned into the NV12 the ports promise before they are handed out.
/// </para>
/// <para>
/// Nothing is converted on PipeWire's thread. That thread is shared with every other client on the machine and
/// holding it up delays all of them, so the process callback copies bytes and returns, and the JPEG decode or 4:2:2
/// rearrangement happens on whichever thread asked for a frame.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class WaylandCameraSource : ICameraSource
{
    /// <summary>Room for a <c>spa_hook</c>, which is opaque and 48 bytes on PipeWire 1.6. Over-allocating is free.</summary>
    private const int HookSize = 64;

    /// <summary>One frame waiting to be converted. More would be a backlog on a live preview.</summary>
    private const int QueueLength = 1;

    private readonly ILogger _logger;
    private readonly Channel<RawFrame> _raw;
    private readonly string _name;
    private readonly ILoggerFactory? _loggers;

    // Held in fields because PipeWire keeps the function pointers: a collected delegate is a call into freed memory.
    private readonly StateChangedCallback _stateChanged;
    private readonly ParamChangedCallback _paramChanged;
    private readonly ProcessCallback _process;

    private nint _loop;
    private nint _context;
    private nint _core;
    private nint _stream;
    private nint _hook;
    private nint _events;
    private GCHandle _self;
    private int _disposed;

    private volatile bool _jpeg;
    private int _width;
    private int _height;
    private FfmpegVideoDecoder? _decoder;

    /// <param name="fileDescriptor">
    /// The descriptor from the camera portal's <c>OpenPipeWireRemote</c> — a connection to the desktop's PipeWire
    /// instance, not a device that can be read from on its own.
    /// </param>
    /// <param name="serial">
    /// Which camera, as <c>object.serial</c>. Not the node id: the two are different numbers and it is the serial
    /// that stays meaningful. The enumerator already knows it, having read it off the registry.
    /// </param>
    /// <param name="camera">What the caller was told this camera is, and gets back untouched.</param>
    public WaylandCameraSource(
        int fileDescriptor,
        uint serial,
        CameraInfo camera,
        ILoggerFactory? loggers = null,
        ILogger<WaylandCameraSource>? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fileDescriptor, 0);
        ArgumentNullException.ThrowIfNull(camera);

        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<WaylandCameraSource>.Instance;
        _loggers = loggers;
        _name = camera.Name;
        Camera = camera;
        _raw = Channel.CreateBounded<RawFrame>(new BoundedChannelOptions(QueueLength)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        _stateChanged = OnStateChanged;
        _paramChanged = OnParamChanged;
        _process = OnProcess;

        Open(fileDescriptor, serial);
    }

    public CameraInfo Camera { get; }

    /// <summary>Which camera this is, as the desktop named it.</summary>
    public override string ToString() => _name;

    /// <summary>
    /// Frames, until the caller stops asking.
    /// </summary>
    /// <remarks>
    /// The format is asked for here rather than when the source was opened, because the interface does not say which
    /// mode is wanted until now — <c>Open</c> takes a camera, not a resolution. The stream is therefore started on
    /// the first frame and stays up for as long as this loop runs.
    /// </remarks>
    public async IAsyncEnumerable<VideoFrame> CaptureAsync(
        CameraMode mode,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mode);

        if (_stream == 0)
        {
            Start(mode);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            RawFrame raw;
            try
            {
                raw = await _raw.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                yield break;
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            var frame = Convert(raw);
            if (frame is not null)
            {
                yield return frame;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _raw.Writer.TryComplete();

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

        PipeWire.ThreadLoopStop(_loop);

        _decoder?.Dispose();
        _decoder = null;

        if (_core != 0)
        {
            PipeWire.ContextDestroy(_context);
            _core = 0;
        }

        PipeWire.ThreadLoopDestroy(_loop);

        Free(_events);
        Free(_hook);
        _events = 0;
        _hook = 0;

        if (_self.IsAllocated)
        {
            _self.Free();
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    private static void Free(nint memory)
    {
        if (memory != 0)
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    /// <summary>Brings up the loop, the connection and the stream. Not the format — that waits for a mode.</summary>
    private unsafe void Open(int fileDescriptor, uint serial)
    {
        PipeWire.Init(0, 0);

        _loop = PipeWire.ThreadLoopNew("remotedesktop-camera", 0);
        if (_loop == 0)
        {
            throw new InvalidOperationException("PipeWire would not create a thread loop.");
        }

        _ = PipeWire.ThreadLoopStart(_loop);

        PipeWire.ThreadLoopLock(_loop);
        try
        {
            var loop = PipeWire.ThreadLoopGetLoop(_loop);
            _context = PipeWire.ContextNew(loop, 0, 0);
            if (_context == 0)
            {
                throw new InvalidOperationException("PipeWire would not create a context.");
            }

            _core = PipeWire.ContextConnectFd(_context, fileDescriptor, 0, 0);
            if (_core == 0)
            {
                throw new InvalidOperationException(
                    $"PipeWire would not connect to the portal's descriptor (errno {Marshal.GetLastPInvokeError()}).");
            }

            // Named by serial, in the properties rather than in pw_stream_connect — the same shape the screen path
            // uses, for the same reason: the target_id argument is deprecated and refused on PipeWire 1.6.
            var properties = PipeWire.PropertiesNewString(
                $"target.object={serial} media.type=Video media.category=Capture media.role=Camera");

            if (properties == 0)
            {
                throw new InvalidOperationException("PipeWire would not create the stream properties.");
            }

            _stream = PipeWire.StreamNew(_core, "remotedesktop-camera", properties);
            if (_stream == 0)
            {
                throw new InvalidOperationException("PipeWire would not create a camera stream.");
            }

            _self = GCHandle.Alloc(this);

            _hook = Marshal.AllocHGlobal(HookSize);
            new Span<byte>((void*)_hook, HookSize).Clear();

            _events = Marshal.AllocHGlobal(Marshal.SizeOf<PipeWire.PwStreamEvents>());
            var events = new PipeWire.PwStreamEvents
            {
                Version = 2,
                StateChanged = Marshal.GetFunctionPointerForDelegate(_stateChanged),
                ParamChanged = Marshal.GetFunctionPointerForDelegate(_paramChanged),
                Process = Marshal.GetFunctionPointerForDelegate(_process),
            };

            Marshal.StructureToPtr(events, _events, fDeleteOld: false);
            PipeWire.StreamAddListener(_stream, _hook, _events, GCHandle.ToIntPtr(_self));
        }
        finally
        {
            PipeWire.ThreadLoopUnlock(_loop);
        }
    }

    /// <summary>Asks the camera for a picture in the mode the caller wants.</summary>
    /// <remarks>
    /// Connected with nothing, then told the format — the same two steps the screen path needs, and for the same
    /// observed reason: the identical pods handed to <c>pw_stream_connect</c> are refused with <c>-EINVAL</c> and no
    /// explanation, while <c>pw_stream_update_params</c> accepts them.
    /// </remarks>
    private unsafe void Start(CameraMode mode)
    {
        _width = mode.Width;
        _height = mode.Height;

        PipeWire.ThreadLoopLock(_loop);
        try
        {
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
                    $"PipeWire refused the camera stream (error {connected}): {DescribeState(_stream)}");
            }

            // JPEG first: a webcam almost always has it, it is a fraction of the bytes, and the decode costs less
            // than moving a raw frame around does. YUY2 second, as the format a camera produces natively when it
            // will not give JPEG — asking for something the device does not have would leave PipeWire inserting a
            // converter between us and the sensor.
            byte[][] pods =
            [
                SpaPodBuilder.EnumFormatJpeg(mode.Width, mode.Height, mode.FrameRate),
                SpaPodBuilder.EnumFormatYuyv(mode.Width, mode.Height, mode.FrameRate),
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
                        $"The camera refused {mode.Width}x{mode.Height} (error {updated}): {DescribeState(_stream)}");
                }
            }
            finally
            {
                foreach (var buffer in buffers)
                {
                    Free(buffer);
                }

                Marshal.FreeHGlobal((nint)parameters);
            }
        }
        finally
        {
            PipeWire.ThreadLoopUnlock(_loop);
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
            _raw.Writer.TryComplete();
        }
        else
        {
            LogStateChanged(_logger, state);
        }
    }

    /// <summary>
    /// Records which of the two requests the camera settled on, which is not a formality: the buffers hold either
    /// JPEG or 4:2:2 depending on the answer, and reading one as the other produces noise rather than an error.
    /// </summary>
    private void OnParamChanged(nint data, uint id, nint param)
    {
        _ = data;

        if (id != PipeWire.SpaParamFormat || param == 0)
        {
            return;
        }

        try
        {
            foreach (var property in SpaPod.Parse(ReadPod(param)).AsProperties())
            {
                // A negotiated value can arrive wrapped, which is how SPA says "this is fixed now".
                var value = property.Value.UnwrapChoice();

                switch (property.Key)
                {
                    case SpaPodBuilder.FormatMediaSubtype when value.Type == SpaPodType.Id:
                        _jpeg = value.AsId() == SpaPodBuilder.MediaSubtypeJpeg;
                        LogSettled(_logger, _jpeg ? "JPEG" : "raw");
                        break;

                    case SpaPodBuilder.FormatVideoSize when value.Type == SpaPodType.Rectangle:
                        var size = value.AsRectangle();
                        _width = size.Width;
                        _height = size.Height;
                        break;

                    default:
                        break;
                }
            }
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        {
            // A format that could not be read is not fatal here: the frames either arrive in a shape this can
            // handle or they do not, and Convert is where that shows up.
            LogFormatUnreadable(_logger, error.Message);
        }
    }

    /// <summary>
    /// Takes the bytes of one frame and returns. Runs on PipeWire's thread, so nothing here decodes or allocates
    /// beyond the copy.
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

            var bytes = new byte[chunk.Size];
            Marshal.Copy(plane.Data + (nint)chunk.Offset, bytes, 0, (int)chunk.Size);
            _raw.Writer.TryWrite(new RawFrame(bytes, (int)chunk.Size));
        }
        finally
        {
            // Always given back, or the stream runs out of buffers and the picture stops.
            _ = PipeWire.StreamQueueBuffer(_stream, raw);
        }
    }

    /// <summary>Turns one frame's bytes into the NV12 the ports promise, on the caller's thread.</summary>
    private VideoFrame? Convert(in RawFrame raw)
    {
        var width = _width;
        var height = _height;
        if (width < 2 || height < 2)
        {
            LogNoFormat(_logger);
            return null;
        }

        var timestamp = TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (_jpeg)
        {
            try
            {
                _decoder ??= new FfmpegVideoDecoder("mjpeg", width, height);

                // Each JPEG is a whole picture, so every frame is a key frame; the decoder never waits for one.
                return _decoder.Decode(new EncodedVideoFrame(raw.Bytes.AsMemory(0, raw.Length), IsKeyFrame: true, timestamp));
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException)
            {
                // A camera that sends one corrupt frame should not end the session; the next one will do.
                LogDecodeFailed(_logger, error.Message);
                return null;
            }
        }

        // 4:2:2: two bytes a pixel, and every row present.
        var stride = width * 2;
        if (raw.Length < stride * height)
        {
            LogTooShort(_logger, raw.Length, stride * height);
            return null;
        }

        var frame = new MemoryVideoFrame(width, height, PixelFormat.Nv12, timestamp);
        CameraFormats.YuyvToNv12(raw.Bytes, stride, width, height, frame.Buffer, width);
        return frame;
    }

    private static unsafe byte[] ReadPod(nint param)
    {
        var header = new ReadOnlySpan<byte>((void*)param, SpaPod.HeaderSize);
        var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header);
        var bytes = new byte[SpaPod.HeaderSize + (int)size];
        Marshal.Copy(param, bytes, 0, bytes.Length);
        return bytes;
    }

    private static unsafe string DescribeState(nint stream)
    {
        var error = (byte*)0;
        var state = PipeWire.StreamGetState(stream, &error);
        var reason = error == null ? "no reason given" : Marshal.PtrToStringUTF8((nint)error) ?? "unreadable reason";
        return $"state {state}: {reason}";
    }

    /// <summary>One frame's bytes, as they arrived.</summary>
    private readonly record struct RawFrame(byte[] Bytes, int Length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StateChangedCallback(nint data, int oldState, int state, nint error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ParamChangedCallback(nint data, uint id, nint param);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ProcessCallback(nint data);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The camera stream is now in state {State}.")]
    private static partial void LogStateChanged(ILogger logger, int state);

    [LoggerMessage(Level = LogLevel.Error, Message = "The camera stream failed: {Reason}")]
    private static partial void LogStreamError(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "The camera settled on {Format}.")]
    private static partial void LogSettled(ILogger logger, string format);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The camera's format could not be read: {Reason}")]
    private static partial void LogFormatUnreadable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A camera frame could not be decoded: {Reason}")]
    private static partial void LogDecodeFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The camera has not said what size its frames are yet, so one was dropped.")]
    private static partial void LogNoFormat(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A camera frame was {Actual} bytes where {Needed} were needed; it was dropped.")]
    private static partial void LogTooShort(ILogger logger, int actual, int needed);
}
