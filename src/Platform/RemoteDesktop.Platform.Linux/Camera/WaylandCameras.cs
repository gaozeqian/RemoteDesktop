using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Platform.Linux.Interop;
using RemoteDesktop.Platform.Linux.XdgPortal;
using RemoteDesktop.Platform.Posix.Interop;

namespace RemoteDesktop.Platform.Linux.Camera;

/// <summary>
/// Cameras as the desktop portal knows them: nodes on the PipeWire graph it opens for us.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not yet run against a camera.</b> This machine reports <c>IsCameraPresent</c> false and has no
/// <c>/dev/video*</c>, so nothing here has ever executed against real hardware. It is written from the portal's
/// own interface definition and patterned on the screen-capture path, which is verified; treat it as a starting
/// point for a machine that has a camera rather than as something known to work.
/// </para>
/// <para>
/// Listing cameras is a question for a person — the portal will not say which cameras exist without first being
/// asked to grant access — so it happens when a list is first wanted, in the same idiom as the control session,
/// and a refusal is left alone for a while rather than asked again on every call. Once granted, the answer is
/// remembered briefly.
/// </para>
/// <para>
/// <b>The fd is not a camera.</b> <c>OpenPipeWireRemote</c> hands back a connection to the desktop's PipeWire
/// instance with every node hidden except the cameras; the cameras themselves are found by reading that graph's
/// registry. There is no V4L2 ioctl anywhere in this, which is what the interface definition says and what an
/// earlier version of the port plan got wrong.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class WaylandCameraEnumerator(
    ILoggerFactory loggerFactory,
    ILogger<WaylandCameraEnumerator> logger) : ICameraEnumerator
{
    /// <summary>How long to leave a refusal alone before asking the desktop again.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>How long a list is believed before it is read again.</summary>
    private static readonly TimeSpan ListLifetime = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to listen to the graph before believing what it said.
    /// </summary>
    /// <remarks>
    /// A fixed window rather than a condition, because there is no "and that is all of them" event to wait for
    /// without binding more of the core interface. The graph is local, so this is time spent on a round trip to a
    /// process that is already running; it is paid once per listing, not per frame.
    /// </remarks>
    private static readonly TimeSpan ScanWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The sizes offered for a camera, none of which has been checked against a device.
    /// </summary>
    /// <remarks>
    /// A camera node can be asked what it supports, through <c>pw_node_enum_params</c> and the arguments that come
    /// back — which is not bound here. Until it is, these are the sizes a webcam is overwhelmingly likely to accept,
    /// offered in the order most previews would want them. A size a device does not have is refused when the stream
    /// is negotiated, which is a failure the operator can see rather than a silent one.
    /// </remarks>
    private static readonly CameraMode[] Modes =
    [
        new(1920, 1080, 30),
        new(1280, 720, 30),
        new(640, 480, 30),
        new(640, 480, 15),
    ];

    private readonly Lock _gate = new();

    private PortalConnection? _connection;
    private DateTime _nextAttempt;
    private DateTime _listedAt;
    private IReadOnlyList<CameraInfo>? _listed;

    /// <inheritdoc />
    public IReadOnlyList<CameraInfo> GetCameras()
    {
        lock (_gate)
        {
            if (_listed is not null && DateTime.UtcNow - _listedAt < ListLifetime)
            {
                return _listed;
            }

            if (DateTime.UtcNow < _nextAttempt)
            {
                return _listed ?? [];
            }

            _nextAttempt = DateTime.UtcNow + RetryDelay;

            try
            {
                var connection = Granted();
                var cameras = new List<CameraInfo>();

                foreach (var node in Read(connection))
                {
                    cameras.Add(new CameraInfo(
                        node.Serial.ToString(CultureInfo.InvariantCulture),
                        node.Name,
                        Modes));
                }

                if (cameras.Count == 0)
                {
                    LogNone(logger);
                }

                _listed = cameras;
                _listedAt = DateTime.UtcNow;
                return cameras;
            }
            catch (Exception error) when (error is PortalException or InvalidOperationException)
            {
                // Asked and refused, or asked and nobody answered. Either way the operator knows nothing more than
                // they did, and asking again on every call would be a dialog per repaint.
                LogRefused(logger, error.Message);
                return _listed ?? [];
            }
        }
    }

    /// <summary>
    /// The connection access was granted on, for the factory that opens a camera to take a descriptor from.
    /// </summary>
    /// <remarks>
    /// Null when nothing has been granted, which is why opening a camera goes through the listing first: the grant
    /// and the list come from the same question, and a factory that reached for a descriptor without it would be
    /// asking the desktop for something it never said yes to.
    /// </remarks>
    internal PortalConnection? ConnectionForOpening()
    {
        lock (_gate)
        {
            return _connection;
        }
    }

    /// <summary>The portal connection with camera access already granted, opening one if there is not one.</summary>
    private PortalConnection Granted()
    {
        if (_connection is not null)
        {
            return _connection;
        }

        LogAsking(logger);

        var connection = new PortalConnection(loggerFactory.CreateLogger<PortalConnection>());
        try
        {
            connection.AccessCameraAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        _connection = connection;
        return connection;
    }

    /// <summary>Reads the camera nodes out of the graph the portal opened.</summary>
    private static List<CameraNode> Read(PortalConnection connection)
    {
        using var remote = connection.OpenCameraPipeWireRemoteAsync(CancellationToken.None).GetAwaiter().GetResult();

        // Duplicated, because the descriptor is handed to PipeWire and it will close what it was given; letting the
        // handle dispose underneath that would close a descriptor now belonging to the connection.
        var descriptor = LibC.DuplicateFileDescriptor((int)remote.DangerousGetHandle());
        return Scan(descriptor);
    }

    /// <summary>
    /// Lists the nodes of a PipeWire remote, keeping the ones that are cameras.
    /// </summary>
    /// <remarks>
    /// The graph is read the way <see cref="Display.PipeWireScreenCapturer"/> reads it for its target, with the same
    /// trap avoided: registry events arrive on the loop's own thread, so the loop lock has to be given back before
    /// waiting for any. Holding it here was what made the first attempt at this see an empty graph.
    /// </remarks>
    private static unsafe List<CameraNode> Scan(int descriptor)
    {
        var cameras = new List<CameraNode>();
        if (descriptor < 0)
        {
            return cameras;
        }

        PipeWire.Init(0, 0);

        var loop = PipeWire.ThreadLoopNew("remotedesktop-cameras", 0);
        if (loop == 0)
        {
            return cameras;
        }

        nint context = 0;
        nint registry = 0;
        nint hook = 0;
        nint events = 0;
        var held = default(GCHandle);

        try
        {
            _ = PipeWire.ThreadLoopStart(loop);

            PipeWire.ThreadLoopLock(loop);
            try
            {
                context = PipeWire.ContextNew(PipeWire.ThreadLoopGetLoop(loop), 0, 0);
                if (context == 0)
                {
                    return cameras;
                }

                var core = PipeWire.ContextConnectFd(context, descriptor, 0, 0);
                if (core == 0)
                {
                    return cameras;
                }

                registry = PipeWire.CoreGetRegistry(core, PipeWire.RegistryVersion, 0);
                if (registry == 0)
                {
                    return cameras;
                }

                RegistryLookup lookup = (_, id, _, type, _, properties) =>
                {
                    if (!string.Equals(Marshal.PtrToStringUTF8(type), "PipeWire:Interface:Node", StringComparison.Ordinal))
                    {
                        return;
                    }

                    // The portal hides everything on this remote except the nodes it granted for the camera role,
                    // so this is its own criterion rather than a guess at what a camera looks like.
                    if (!TryGetProperty(properties, "media.role", out var role) || !string.Equals(role, "Camera", StringComparison.Ordinal))
                    {
                        return;
                    }

                    if (!TryGetProperty(properties, "object.serial", out var text)
                        || !uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var serial)
                        || serial == 0)
                    {
                        return;
                    }

                    var name = TryGetProperty(properties, "node.description", out var description) && description.Length > 0
                        ? description
                        : TryGetProperty(properties, "node.name", out var nodeName) && nodeName.Length > 0
                            ? nodeName
                            : $"Camera {id}";

                    cameras.Add(new CameraNode(serial, name));
                };

                held = GCHandle.Alloc(lookup);
                hook = Marshal.AllocHGlobal(HookSize);
                new Span<byte>((void*)hook, HookSize).Clear();
                events = Marshal.AllocHGlobal(Marshal.SizeOf<PipeWire.PwRegistryEvents>());
                Marshal.StructureToPtr(
                    new PipeWire.PwRegistryEvents { Version = 0, Global = Marshal.GetFunctionPointerForDelegate(lookup) },
                    events,
                    fDeleteOld: false);

                PipeWire.RegistryAddListener(registry, hook, events, 0);
            }
            finally
            {
                PipeWire.ThreadLoopUnlock(loop);
            }

            Thread.Sleep(ScanWindow);
        }
        finally
        {
            if (registry != 0 || context != 0)
            {
                PipeWire.ThreadLoopLock(loop);
                try
                {
                    if (registry != 0)
                    {
                        PipeWire.RegistryDestroy(registry);
                    }

                    if (context != 0)
                    {
                        PipeWire.ContextDestroy(context);
                    }
                }
                finally
                {
                    PipeWire.ThreadLoopUnlock(loop);
                }
            }

            PipeWire.ThreadLoopStop(loop);
            PipeWire.ThreadLoopDestroy(loop);

            if (events != 0)
            {
                Marshal.FreeHGlobal(events);
            }

            if (hook != 0)
            {
                Marshal.FreeHGlobal(hook);
            }

            if (held.IsAllocated)
            {
                held.Free();
            }
        }

        return cameras;
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

    private const int HookSize = 64;

    /// <summary>A camera as the graph describes it: what it can be named by, and what to call it.</summary>
    private readonly record struct CameraNode(uint Serial, string Name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RegistryLookup(nint data, uint id, uint permissions, nint type, uint version, nint properties);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Listing cameras needs the desktop's permission, so a dialog is about to ask. It asks every time the program starts, because camera access cannot be granted in advance.")]
    private static partial void LogAsking(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The desktop did not grant access to the cameras: {Reason}")]
    private static partial void LogRefused(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "The desktop granted camera access and there are no cameras on this machine.")]
    private static partial void LogNone(ILogger logger);
}

/// <summary>
/// Opens a camera the enumerator listed.
/// </summary>
/// <remarks>
/// The id it was listed under is the node's serial, which is what the stream has to be pointed at. The descriptor
/// is taken fresh here rather than kept from the listing: the portal hands one out per call, and a connection held
/// open since the list was drawn would be one more thing to keep alive for no gain.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class WaylandCameraSourceFactory(
    WaylandCameraEnumerator enumerator,
    ILoggerFactory loggerFactory,
    ILogger<WaylandCameraSourceFactory> logger) : ICameraSourceFactory
{
    public ICameraSource Open(string cameraId)
    {
        ArgumentException.ThrowIfNullOrEmpty(cameraId);

        if (!uint.TryParse(cameraId, NumberStyles.None, CultureInfo.InvariantCulture, out var serial) || serial == 0)
        {
            throw new ArgumentException($"'{cameraId}' is not a camera this platform listed.", nameof(cameraId));
        }

        var camera = Find(cameraId)
            ?? throw new InvalidOperationException($"No camera '{cameraId}' — it was listed, but it is not there now.");

        var connection = Connection()
            ?? throw new InvalidOperationException("Camera access has not been granted, so no camera can be opened.");

        var descriptor = LibC.DuplicateFileDescriptor(
            (int)connection.OpenCameraPipeWireRemoteAsync(CancellationToken.None).GetAwaiter().GetResult().DangerousGetHandle());

        LogOpening(logger, camera.Name, serial);
        return new WaylandCameraSource(
            descriptor,
            serial,
            camera,
            loggerFactory,
            loggerFactory.CreateLogger<WaylandCameraSource>());
    }

    private CameraInfo? Find(string cameraId)
    {
        foreach (var camera in enumerator.GetCameras())
        {
            if (string.Equals(camera.Id, cameraId, StringComparison.Ordinal))
            {
                return camera;
            }
        }

        return null;
    }

    private PortalConnection? Connection() =>
        enumerator.GetCameras().Count > 0 ? enumerator.ConnectionForOpening() : null;

    [LoggerMessage(Level = LogLevel.Information, Message = "Opening camera '{Camera}' (node serial {Serial}).")]
    private static partial void LogOpening(ILogger logger, string camera, uint serial);
}
