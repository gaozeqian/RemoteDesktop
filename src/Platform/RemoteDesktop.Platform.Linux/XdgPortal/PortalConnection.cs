using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using RemoteDesktop.Platform.Posix.Interop;
using Tmds.DBus.Protocol;

namespace RemoteDesktop.Platform.Linux.XdgPortal;

/// <summary>
/// One screen the compositor is sharing, as the portal describes it.
/// </summary>
/// <remarks>
/// The portal calls this a "stream" — each shared screen is a PipeWire stream. It is named for what it is here
/// rather than for the transport, because the callers enumerate screens and never think about PipeWire.
/// </remarks>
/// <param name="NodeId">The PipeWire node to read the pictures from.</param>
/// <param name="X">Where the screen sits in the desktop, in the compositor's coordinates.</param>
/// <param name="Y">The same, vertically.</param>
/// <param name="Width">How wide it is, in the same coordinates.</param>
/// <param name="Height">How tall it is, in the same coordinates.</param>
public readonly record struct SharedScreen(uint NodeId, int X, int Y, int Width, int Height);

/// <summary>
/// Talks to the desktop portals over the session bus.
/// </summary>
/// <remarks>
/// <para>
/// The portal's request model shapes this whole class, and two of its rules are the reason it looks the way it does.
/// </para>
/// <para>
/// The first is that a method call answers twice: once with a handle to the request, and later with a
/// <c>Response</c> signal on that handle's object path. The path can be computed in advance from the caller's bus
/// name and a token the caller chooses — which is the only reason it is possible to subscribe before calling, as the
/// portal requires. The token is made here and handed back to each caller so that what goes into the request and
/// what the match rule was installed for cannot drift apart; an earlier version generated them separately, which
/// meant every answer arrived at a path nobody was listening on.
/// </para>
/// <para>
/// The second rule is a constraint of the D-Bus library rather than of the portal: <c>MessageWriter</c> is a
/// <c>ref struct</c>, and passing one through a delegate corrupts memory — measured, not guessed: a connection that
/// was working would drop the moment a body was written through a lambda, with the process aborting. So each call
/// writes its own body, in the method that holds the writer, and there is no shared "write the body" helper. It is
/// more repetition than a helper would be, and it is the only shape that works.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class PortalConnection : IDisposable
{
    private const string Destination = "org.freedesktop.portal.Desktop";
    private const string ObjectPath = "/org/freedesktop/portal/desktop";
    private const string ScreenCast = "org.freedesktop.portal.ScreenCast";
    private const string RemoteDesktop = "org.freedesktop.portal.RemoteDesktop";
    private const string Clipboard = "org.freedesktop.portal.Clipboard";
    private const string Camera = "org.freedesktop.portal.Camera";
    private const string RequestInterface = "org.freedesktop.portal.Request";

    /// <summary>SPA_SOURCE_MONITOR — a whole screen rather than one window.</summary>
    public const uint SourceMonitor = 1;

    /// <summary>
    /// SPA_CURSOR_METADATA — the pointer is described separately instead of being painted into the picture.
    /// </summary>
    /// <remarks>
    /// The one that matters. <c>Embedded</c> bakes the pointer into the pixels, and the controller draws the pointer
    /// itself from the cursor events the capturer raises; embedded would mean two pointers on screen, and one of
    /// them a frame behind. The compositor here offers all three modes, which a test checks.
    /// </remarks>
    public const uint CursorMetadata = 4;

    /// <summary>Persist while the session lasts: the grant is remembered for as long as this session object lives.</summary>
    public const uint PersistWhileRunning = 1;

    /// <summary>Persist across restarts, so the operator is not asked again on every connection.</summary>
    public const uint PersistPermanently = 2;

    /// <summary>SPA_DEVICE_KEYBOARD | SPA_DEVICE_POINTER.</summary>
    public const uint DevicesKeyboardAndPointer = 3;

    private readonly DBusConnection _connection;
    private readonly ILogger _logger;
    private readonly string _sender;

    /// <param name="logger">
    /// Optional, and worth supplying in a host: establishing a session waits on a person, and when that person is not
    /// there the only symptom is a request that never returns. Which request it was is the whole diagnosis.
    /// </param>
    public PortalConnection(ILogger<PortalConnection>? logger = null)
    {
        _logger = logger ?? NullLogger<PortalConnection>.Instance;

        // Blocking here, unlike everywhere else in this class: a constructor cannot await, and this is the one call
        // that has to finish before anything else can be built on top of it.
        var address = DBusAddress.Session
            ?? throw new PortalException("There is no session bus, so the desktop portals cannot be reached.");

        _connection = new DBusConnection(address);
        _connection.ConnectAsync().AsTask().GetAwaiter().GetResult();
        _sender = _connection.UniqueName!.TrimStart(':').Replace('.', '_');
    }

    /// <summary>What this compositor can do, asked of it rather than assumed.</summary>
    public async Task<(uint SourceTypes, uint CursorModes, uint Version)> ScreenCastCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var sourceTypes = await ReadPropertyAsync(ScreenCast, "AvailableSourceTypes", cancellationToken).ConfigureAwait(false);
        var cursorModes = await ReadPropertyAsync(ScreenCast, "AvailableCursorModes", cancellationToken).ConfigureAwait(false);
        var version = await ReadPropertyAsync(ScreenCast, "version", cancellationToken).ConfigureAwait(false);
        return (sourceTypes, cursorModes, version);
    }

    /// <summary>Opens a screen-cast session and asks for a screen, waiting for the operator to agree.</summary>
    /// <param name="restoreToken">
    /// What the portal gave back last time, or <c>null</c> for a first run. With one, the portal can skip asking.
    /// </param>
    /// <param name="onRestoreToken">Called with a fresh token, so the caller can keep it for next time.</param>
    /// <returns>The session handle and the screens the operator agreed to share.</returns>
    /// <exception cref="PortalException">The operator refused, or the portal reported a failure.</exception>
    /// <remarks>
    /// The dialog appears at <c>Start</c>. Everything before it is bookkeeping the operator never sees.
    /// </remarks>
    public async Task<(string SessionHandle, IReadOnlyList<SharedScreen> Streams)> OpenScreenCastAsync(
        string? restoreToken,
        Action<string>? onRestoreToken,
        CancellationToken cancellationToken)
    {
        var session = await CreateSessionAsync(ScreenCast, cancellationToken).ConfigureAwait(false);

        var selected = await SelectSourcesAsync(session, restoreToken, persist: true, cancellationToken).ConfigureAwait(false);
        if (selected != 0)
        {
            throw new PortalException(selected, "The screen could not be selected.");
        }

        var (started, streams, token) = await StartAsync(ScreenCast, session, cancellationToken).ConfigureAwait(false);
        if (started != 0)
        {
            throw new PortalException(started, "Screen sharing was not granted.");
        }

        if (!string.IsNullOrEmpty(token))
        {
            onRestoreToken?.Invoke(token);
        }

        return (session, streams);
    }

    /// <summary>
    /// Opens a remote-control session and asks for the keyboard and the pointer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A session of its own rather than sharing the screen-cast one, and the reason is a restriction in the portal
    /// that was found by trying it: <em>a remote-desktop session cannot persist</em>. Asking for
    /// <c>persist_mode</c> on this kind of session is refused outright — "Remote desktop sessions cannot persist" —
    /// so a combined session would mean the operator answering the same dialog on every single connection. Input
    /// control is the more dangerous half of what this product does, and the portal is deliberate about not letting
    /// it be granted in advance.
    /// </para>
    /// <para>
    /// Keeping the two apart means screen sharing stays unattended — one grant, remembered, as long as the desktop
    /// is willing — while control is asked for when it is actually needed, and only then.
    /// </para>
    /// </remarks>
    public async Task<string> OpenRemoteDesktopAsync(CancellationToken cancellationToken) =>
        await OpenRemoteDesktopAsync(withClipboard: false, cancellationToken).ConfigureAwait(false);

    /// <param name="withClipboard">
    /// Whether to also ask for the clipboard. It has to be asked for <em>before</em> <c>Start</c>: the portal allows
    /// it only while the session is still being set up, and refuses afterwards with "Invalid state".
    /// </param>
    public async Task<string> OpenRemoteDesktopAsync(bool withClipboard, CancellationToken cancellationToken)
    {
        var session = await CreateSessionAsync(RemoteDesktop, cancellationToken).ConfigureAwait(false);

        var devices = await SelectDevicesAsync(session, cancellationToken).ConfigureAwait(false);
        if (devices != 0)
        {
            throw new PortalException(devices, "The keyboard and pointer were not granted.");
        }

        if (withClipboard)
        {
            await RequestClipboardAsync(session, cancellationToken).ConfigureAwait(false);
        }

        // Start is what makes the session usable, and on this path it is also where the dialog appears. A session
        // that has been created and had its devices chosen but not started refuses everything else with
        // "Session is not ready" — including the call that would hand over the descriptor to inject input on.
        var (started, _, _) = await StartAsync(RemoteDesktop, session, cancellationToken).ConfigureAwait(false);
        if (started != 0)
        {
            throw new PortalException(started, "Control of this screen was not granted.");
        }

        return session;
    }

    /// <summary>The descriptor PipeWire's pictures are read from.</summary>
    /// <remarks>
    /// Unlike the other calls this one answers immediately with a descriptor instead of returning a request handle, so
    /// there is no match rule to install — the screening decision was already made by <c>Start</c>.
    /// </remarks>
    public Task<SafeFileHandle> OpenPipeWireRemoteAsync(string session, CancellationToken cancellationToken) =>
        CallForFdAsync(ScreenCast, "OpenPipeWireRemote", session, FdArgument.None, text: null, serial: 0, cancellationToken);

    /// <summary>The descriptor the input-injection session is spoken over (libei's transport).</summary>
    public Task<SafeFileHandle> ConnectToEisAsync(string session, CancellationToken cancellationToken) =>
        CallForFdAsync(RemoteDesktop, "ConnectToEIS", session, FdArgument.None, text: null, serial: 0, cancellationToken);

    /// <summary>
    /// Whether this machine has a camera at all.
    /// </summary>
    /// <remarks>
    /// A property, not a request: reading it asks nobody anything and raises no dialog, so it is safe to ask on a
    /// machine that may have no camera — which is the only reason the enumerator can answer honestly without
    /// putting a question in front of somebody.
    /// </remarks>
    public async Task<bool> CameraPresentAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var w = _connection.GetMessageWriter();
        w.WriteMethodCallHeader(Destination, ObjectPath, "org.freedesktop.DBus.Properties", "Get", "ss", MessageFlags.None);
        w.WriteString(Camera);
        w.WriteString("IsCameraPresent");
        var message = w.CreateMessage();

        return await _connection.CallMethodAsync(
            message,
            static (Message m, object? _) => m.GetBodyReader().ReadVariantValue().GetBool(),
            null).ConfigureAwait(false);
    }

    /// <summary>Asks the desktop to let this program at the cameras. A question for a person.</summary>
    /// <remarks>
    /// Separate from opening one on purpose: the portal's own documentation puts the ask here and the descriptor in
    /// <see cref="OpenCameraPipeWireRemoteAsync"/>, so the dialog happens once and the descriptor can then be taken
    /// as often as the program likes.
    /// </remarks>
    public async Task AccessCameraAsync(CancellationToken cancellationToken)
    {
        var (token, pending) = await PrepareAsync().ConfigureAwait(false);

        var w = _connection.GetMessageWriter();
        w.WriteMethodCallHeader(Destination, ObjectPath, Camera, "AccessCamera", "a{sv}", MessageFlags.None);
        w.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = token });
        var message = w.CreateMessage();

        // The request object, which is not what is being waited for — the answer is the signal.
        _ = await _connection
            .CallMethodAsync(message, static (Message m, object? _) => m.GetBodyReader().ReadObjectPathAsString(), null)
            .ConfigureAwait(false);

        var result = await AwaitAsync(pending, "AccessCamera", cancellationToken).ConfigureAwait(false);
        if (result.Response != 0)
        {
            throw new PortalException(result.Response, "The desktop did not grant access to the camera.");
        }
    }

    /// <summary>
    /// The PipeWire remote the camera nodes are on.
    /// </summary>
    /// <remarks>
    /// <b>Not a camera device.</b> The interface defined in <c>org.freedesktop.portal.Camera</c> says the descriptor
    /// "should be used to create a <c>pw_core</c> object, by using <c>pw_context_connect_fd()</c>": the cameras are
    /// nodes in a PipeWire graph, reached exactly the way a captured screen is, and there are no V4L2 ioctls
    /// anywhere in this. The portal hides every other node on that remote, so what is reached through it is the
    /// cameras and nothing else.
    /// </remarks>
    public Task<SafeFileHandle> OpenCameraPipeWireRemoteAsync(CancellationToken cancellationToken) =>
        CallForFdAsync(Camera, "OpenPipeWireRemote", session: string.Empty, FdArgument.Options, text: null, serial: 0, cancellationToken);

    /// <summary>
    /// Types a character by its keysym, for text that has no key.
    /// </summary>
    /// <remarks>
    /// libei's keyboard takes evdev key codes, so an emoji or an input-method result cannot be expressed by pressing
    /// a key at all. This is the portal's own call for that case: a keysym names the character rather than the key,
    /// and Unicode is mapped to one by putting the code point under <c>0x01000000</c>.
    /// </remarks>
    public async Task NotifyKeyboardKeysymAsync(
        string session,
        uint keysym,
        bool pressed,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var w = _connection.GetMessageWriter();
        w.WriteMethodCallHeader(Destination, ObjectPath, RemoteDesktop, "NotifyKeyboardKeysym", "oa{sv}iu", MessageFlags.None);
        w.WriteObjectPath(session);
        w.WriteDictionary(new Dictionary<string, VariantValue>());

        // Keysym, then the state: the portal reads it as an int, 1 for pressed.
        w.WriteUInt32(keysym);
        w.WriteUInt32(pressed ? 1u : 0u);
        var message = w.CreateMessage();

        // Answers with nothing, so there is no body to read.
        _ = await _connection
            .CallMethodAsync(message, static (Message m, object? _) => true, null)
            .ConfigureAwait(false);
    }

    /// <summary>Asks for the clipboard to be handled over this session. Must be before <c>Start</c>.</summary>
    /// <remarks>
    /// The portal's own words: "This request must be made before the session starts. The session must be started,
    /// before using any other method in this interface which take a session." The clipboard is one of the things the
    /// dialog grants, so it is asked for while the session is still being set up — afterwards the portal answers
    /// "Invalid state" and there is no second chance on that session.
    /// </remarks>
    public async Task RequestClipboardAsync(string session, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var w = _connection.GetMessageWriter();
        w.WriteMethodCallHeader(Destination, ObjectPath, Clipboard, "RequestClipboard", "oa{sv}", MessageFlags.None);
        w.WriteObjectPath(session);
        w.WriteDictionary(new Dictionary<string, VariantValue>());
        var message = w.CreateMessage();

        _ = await _connection.CallMethodAsync(message, static (Message m, object? _) => true, null).ConfigureAwait(false);
    }

    /// <summary>Claims ownership of the clipboard for this session, advertising the formats it can supply.</summary>
    public async Task SetSelectionAsync(string session, IReadOnlyList<string> mimeTypes, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var w = _connection.GetMessageWriter();
        w.WriteMethodCallHeader(Destination, ObjectPath, Clipboard, "SetSelection", "oa{sv}", MessageFlags.None);
        w.WriteObjectPath(session);
        w.WriteDictionary(new Dictionary<string, VariantValue>
        {
            ["mime_types"] = VariantValue.Array(mimeTypes.ToArray()),
        });
        var message = w.CreateMessage();

        _ = await _connection.CallMethodAsync(message, static (Message m, object? _) => true, null).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the current selection in one format. The descriptor is the reading end of a pipe.
    /// </summary>
    public Task<SafeFileHandle> SelectionReadAsync(string session, string mimeType, CancellationToken cancellationToken) =>
        CallForFdAsync(Clipboard, "SelectionRead", session, FdArgument.Text, mimeType, serial: 0, cancellationToken);

    /// <summary>
    /// Answers a <c>SelectionTransfer</c>: the descriptor is the writing end of a pipe the other side reads from.
    /// </summary>
    public Task<SafeFileHandle> SelectionWriteAsync(string session, uint serial, CancellationToken cancellationToken) =>
        CallForFdAsync(Clipboard, "SelectionWrite", session, FdArgument.Serial, text: null, serial, cancellationToken);

    /// <summary>Says whether a transfer was answered, so the other side is not left waiting on the pipe.</summary>
    public async Task SelectionWriteDoneAsync(string session, uint serial, bool success, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var w = _connection.GetMessageWriter();
        w.WriteMethodCallHeader(Destination, ObjectPath, Clipboard, "SelectionWriteDone", "oub", MessageFlags.None);
        w.WriteObjectPath(session);
        w.WriteUInt32(serial);
        w.WriteBool(success);
        var message = w.CreateMessage();

        _ = await _connection.CallMethodAsync(message, static (Message m, object? _) => true, null).ConfigureAwait(false);
    }

    /// <summary>
    /// Watches for the clipboard changing, and for the other side asking for its contents.
    /// </summary>
    /// <remarks>
    /// Matched on interface and member only, then filtered by session here. The signals carry their session as the
    /// first argument rather than being emitted on the session's own path, so there is nothing narrower to match on
    /// — and the alternative, a match rule per session, would have to be torn down by hand every time one closed.
    /// </remarks>
    public async Task WatchClipboardAsync(
        string session,
        Action<IReadOnlyList<string>, bool> onOwnerChanged,
        Action<string, uint> onTransfer,
        CancellationToken cancellationToken)
    {
        await _connection.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Interface = Clipboard, Member = "SelectionOwnerChanged" },
            static (Message m, object? _) => ReadOwnerChanged(m),
            (Notification<(string Session, IReadOnlyList<string> Types, bool IsOwner)> notification) =>
            {
                if (notification.HasValue && notification.Value.Session == session)
                {
                    onOwnerChanged(notification.Value.Types, notification.Value.IsOwner);
                }
            },
            emitOnCapturedContext: false, ObserverFlags.None, null).ConfigureAwait(false);

        await _connection.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Interface = Clipboard, Member = "SelectionTransfer" },
            static (Message m, object? _) => ReadTransfer(m),
            (Notification<(string Session, string MimeType, uint Serial)> notification) =>
            {
                if (notification.HasValue && notification.Value.Session == session)
                {
                    onTransfer(notification.Value.MimeType, notification.Value.Serial);
                }
            },
            emitOnCapturedContext: false, ObserverFlags.None, null).ConfigureAwait(false);

        _ = cancellationToken;
    }

    private static (string Session, IReadOnlyList<string> Types, bool IsOwner) ReadOwnerChanged(Message message)
    {
        var reader = message.GetBodyReader();
        var session = reader.ReadObjectPathAsString();
        var types = new List<string>();
        var isOwner = false;

        foreach (var (key, value) in reader.ReadDictionaryOfStringToVariantValue())
        {
            if (key == "mime_types" && value.Type == VariantValueType.Array)
            {
                for (var i = 0; i < value.Count; i++)
                {
                    types.Add(value.GetItem(i).GetString());
                }
            }
            else if (key == "session_is_owner" && value.Type == VariantValueType.Bool)
            {
                isOwner = value.GetBool();
            }
        }

        return (session, types, isOwner);
    }

    private static (string Session, string MimeType, uint Serial) ReadTransfer(Message message)
    {
        var reader = message.GetBodyReader();
        return (reader.ReadObjectPathAsString(), reader.ReadString(), reader.ReadUInt32());
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>
    /// Subscribes to the response of the call that is about to be made, and makes its token.
    /// </summary>
    /// <remarks>
    /// Awaited rather than blocked on: the reply is delivered by the connection's own read loop, and waiting on it
    /// from this thread stops that loop from running. An earlier version returned the handle synchronously and had to
    /// block; it deadlocked.
    /// </remarks>
    private async Task<(string Token, TaskCompletionSource<PortalRequestResult> Pending)> PrepareAsync()
    {
        var token = "rd" + Guid.NewGuid().ToString("N")[..12];
        var path = $"/org/freedesktop/portal/desktop/request/{_sender}/{token}";
        var pending = new TaskCompletionSource<PortalRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        await _connection.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Path = path, Interface = RequestInterface, Member = "Response" },
            static (Message message, object? _) => PortalRequestResult.Read(message),
            (Notification<PortalRequestResult> notification) => OnNotification(pending, notification),
            emitOnCapturedContext: false, ObserverFlags.None, null).ConfigureAwait(false);

        LogSubscribed(_logger, path);
        return (token, pending);
    }

    private async Task<string> CreateSessionAsync(string @interface, CancellationToken cancellationToken)
    {
        var (token, pending) = await PrepareAsync().ConfigureAwait(false);

        var w = _connection.GetMessageWriter();
        w.WriteMethodCallHeader(Destination, ObjectPath, @interface, "CreateSession", "a{sv}", MessageFlags.None);
        w.WriteDictionary(new Dictionary<string, VariantValue>
        {
            // The token is the one the match rule above was installed for. Both come from the same place, which is
            // what makes them impossible to disagree.
            ["handle_token"] = token,
            ["session_handle_token"] = "rdSession" + Guid.NewGuid().ToString("N")[..8],
        });
        var message = w.CreateMessage();

        _ = await _connection
            .CallMethodAsync(message, static (Message m, object? _) => m.GetBodyReader().ReadObjectPathAsString(), null)
            .ConfigureAwait(false);

        var result = await AwaitAsync(pending, "CreateSession", cancellationToken).ConfigureAwait(false);
        if (result.Response != 0 || result.SessionHandle is not { Length: > 0 } handle)
        {
            throw new PortalException(result.Response, "A portal session could not be opened.");
        }

        return handle;
    }

    private async Task<uint> SelectSourcesAsync(string session, string? restoreToken, bool persist, CancellationToken cancellationToken)
    {
        var (token, pending) = await PrepareAsync().ConfigureAwait(false);

        var options = new Dictionary<string, VariantValue>
        {
            ["handle_token"] = token,
            ["types"] = SourceMonitor,
            ["cursor_mode"] = CursorMetadata,
            ["multiple"] = false,
        };

        if (persist)
        {
            // Persist across restarts, so that a machine being controlled does not need somebody at the keyboard
            // every time a controller connects — which is the whole point of unattended access. Not allowed on a
            // remote-desktop session, which is why it is optional.
            options["persist_mode"] = PersistPermanently;
        }

        if (!string.IsNullOrEmpty(restoreToken))
        {
            options["restore_token"] = restoreToken;
        }

        var w = _connection.GetMessageWriter();
        w.WriteMethodCallHeader(Destination, ObjectPath, ScreenCast, "SelectSources", "oa{sv}", MessageFlags.None);
        w.WriteObjectPath(session);
        w.WriteDictionary(options);
        var message = w.CreateMessage();

        _ = await _connection
            .CallMethodAsync(message, static (Message m, object? _) => m.GetBodyReader().ReadObjectPathAsString(), null)
            .ConfigureAwait(false);

        var result = await AwaitAsync(pending, "SelectSources", cancellationToken).ConfigureAwait(false);
        return result.Response;
    }

    private async Task<uint> SelectDevicesAsync(string session, CancellationToken cancellationToken)
    {
        var (token, pending) = await PrepareAsync().ConfigureAwait(false);

        var w = _connection.GetMessageWriter();
        w.WriteMethodCallHeader(Destination, ObjectPath, RemoteDesktop, "SelectDevices", "oa{sv}", MessageFlags.None);
        w.WriteObjectPath(session);
        w.WriteDictionary(new Dictionary<string, VariantValue>
        {
            ["handle_token"] = token,
            ["types"] = DevicesKeyboardAndPointer,

            // No persist_mode. A remote-desktop session may not be persisted — asking is refused with
            // "Remote desktop sessions cannot persist" — which is the portal being deliberate about not letting
            // control over a machine be granted in advance.
        });
        var message = w.CreateMessage();

        _ = await _connection
            .CallMethodAsync(message, static (Message m, object? _) => m.GetBodyReader().ReadObjectPathAsString(), null)
            .ConfigureAwait(false);

        var result = await AwaitAsync(pending, "SelectDevices", cancellationToken).ConfigureAwait(false);
        return result.Response;
    }

    private async Task<(uint Response, IReadOnlyList<SharedScreen> Streams, string? RestoreToken)> StartAsync(
        string @interface, string session, CancellationToken cancellationToken)
    {
        var (token, pending) = await PrepareAsync().ConfigureAwait(false);

        var w = _connection.GetMessageWriter();
        w.WriteMethodCallHeader(Destination, ObjectPath, @interface, "Start", "osa{sv}", MessageFlags.None);
        w.WriteObjectPath(session);
        w.WriteString(string.Empty);
        w.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = token });
        var message = w.CreateMessage();

        _ = await _connection
            .CallMethodAsync(message, static (Message m, object? _) => m.GetBodyReader().ReadObjectPathAsString(), null)
            .ConfigureAwait(false);

        var result = await AwaitAsync(pending, "Start", cancellationToken).ConfigureAwait(false);
        return (result.Response, result.Streams, result.RestoreToken);
    }

    /// <summary>What a descriptor-returning call takes besides the session, which varies by call.</summary>
    private enum FdArgument
    {
        /// <summary>Just the session. <c>OpenPipeWireRemote</c> and <c>ConnectToEIS</c>, both <c>oa{sv}</c>.</summary>
        None,

        /// <summary>
        /// No session at all. The camera's <c>OpenPipeWireRemote</c>, which is <c>a{sv}</c>: the cameras belong to
        /// the machine and to the person in front of it, and are not part of anything that was granted per session.
        /// </summary>
        Options,

        /// <summary>A mime type. <c>SelectionRead</c>, which is <c>os</c>.</summary>
        Text,

        /// <summary>A transfer serial. <c>SelectionWrite</c>, which is <c>ou</c>.</summary>
        Serial,
    }

    /// <summary>Makes a call that answers with a file descriptor rather than a request handle.</summary>
    /// <remarks>
    /// Each of these four has its own signature, and the portal rejects a message whose signature is not the one it
    /// declared — <c>InvalidArgs: Type of message, "(osa{sv})", does not match expected type "(os)"</c>. There is no
    /// writing one shape and hoping, so the shape is chosen here rather than at each call site. It also has to be
    /// written inline: the writer is a <c>ref struct</c> and cannot be handed to a helper.
    /// </remarks>
    private async Task<SafeFileHandle> CallForFdAsync(
        string @interface,
        string member,
        string session,
        FdArgument argument,
        string? text,
        uint serial,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var w = _connection.GetMessageWriter();
        w.WriteMethodCallHeader(
            Destination,
            ObjectPath,
            @interface,
            member,
            argument switch
            {
                FdArgument.Options => "a{sv}",
                FdArgument.Text => "os",
                FdArgument.Serial => "ou",
                _ => "oa{sv}",
            },
            MessageFlags.None);

        if (argument != FdArgument.Options)
        {
            w.WriteObjectPath(session);
        }

        switch (argument)
        {
            case FdArgument.Text:
                w.WriteString(text ?? string.Empty);
                break;

            case FdArgument.Serial:
                w.WriteUInt32(serial);
                break;

            default:
                w.WriteDictionary(new Dictionary<string, VariantValue>());
                break;
        }

        var message = w.CreateMessage();

        // Duplicated inside the reader, not after the call returns. The descriptor belongs to the message, and the
        // message is disposed as soon as this delegate has run — so duplicating afterwards is a race that usually
        // works and occasionally duplicates a number that has already been closed, which reads as EBADF on a
        // connection that is perfectly healthy.
        int copy;
        try
        {
            copy = await _connection
                .CallMethodAsync(
                    message,
                    static (Message m, object? _) => LibC.DuplicateFileDescriptor((int)m.GetBodyReader().ReadHandleRaw()),
                    null)
                .ConfigureAwait(false);
        }
        catch (DBusExceptionBase error)
        {
            // The rest of this class reports the portal's refusals as PortalException, and callers catch exactly
            // that. Letting the D-Bus error type through turned an ordinary refusal into a failure nobody expected.
            throw new PortalException($"The portal refused {member}: {error.Message}", error);
        }

        if (copy < 0)
        {
            throw new PortalException($"The portal's descriptor could not be duplicated (errno {Marshal.GetLastPInvokeError()}).");
        }

        return new SafeFileHandle(copy, ownsHandle: true);
    }

    /// <summary>
    /// Turns whatever the subscription reports into either a value or a failure.
    /// </summary>
    /// <remarks>
    /// Every outcome except <c>Value</c> has to complete the wait. An earlier version only looked at the value and
    /// ignored the rest, which meant that an answer the reader could not parse — or a connection that had dropped —
    /// arrived at the caller as nothing at all, and the caller sat there until its own timeout and was then told the
    /// portal had not replied. It had. The report was being thrown away. A wait that can never be satisfied is the
    /// worst possible way to learn that something is wrong.
    /// </remarks>
    private void OnNotification(TaskCompletionSource<PortalRequestResult> pending, in Notification<PortalRequestResult> notification)
    {
        if (notification.HasValue)
        {
            pending.TrySetResult(notification.Value);
            return;
        }

        var reason = notification.Exception?.Message ?? notification.Type.ToString();
        LogSubscriptionFailed(_logger, notification.Type.ToString(), reason);
        pending.TrySetException(new PortalException($"The portal's answer could not be received: {reason}"));
    }

    private async Task<PortalRequestResult> AwaitAsync(
        TaskCompletionSource<PortalRequestResult> pending, string member, CancellationToken cancellationToken)
    {
        // Logged before the wait, not after: the requests that matter are the ones where somebody has to walk over to
        // the machine and agree, and while that is happening the process looks like it has simply stopped.
        LogWaiting(_logger, member);

        try
        {
            var result = await pending.Task.WaitAsync(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);

            // The answer arrived but could not be understood. Said plainly, because the alternative is the caller
            // concluding the desktop never replied and going to look at the wrong end of the problem entirely.
            if (result.Failure is { Length: > 0 } failure)
            {
                throw new PortalException($"The portal's answer to {member} could not be read: {failure}");
            }

            LogAnswered(_logger, member, result.Response);
            return result;
        }
        catch (TimeoutException)
        {
            throw new PortalException($"The portal never answered {member}'s request.");
        }
    }

    private async Task<uint> ReadPropertyAsync(string @interface, string name, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var w = _connection.GetMessageWriter();
        w.WriteMethodCallHeader(Destination, ObjectPath, "org.freedesktop.DBus.Properties", "Get", "ss", MessageFlags.None);
        w.WriteString(@interface);
        w.WriteString(name);
        var message = w.CreateMessage();

        return await _connection.CallMethodAsync(message, static (Message m, object? _) =>
        {
            var reader = m.GetBodyReader();
            var value = reader.ReadVariantValue();
            return value.GetUInt32();
        }, null).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Listening for the answer to this request on {Path}.")]
    private static partial void LogSubscribed(ILogger logger, string path);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Waiting for the portal to answer {Member}. If the desktop is asking whether to allow this, somebody has to answer it.")]
    private static partial void LogWaiting(ILogger logger, string member);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The portal answered {Member} with {Response}.")]
    private static partial void LogAnswered(ILogger logger, string member, uint response);

    [LoggerMessage(Level = LogLevel.Error, Message = "The portal's answer arrived as {Kind} and could not be used: {Reason}")]
    private static partial void LogSubscriptionFailed(ILogger logger, string kind, string reason);
}

/// <summary>Why a portal request did not succeed.</summary>
public sealed class PortalException : Exception
{
    /// <summary>The portal's own answer: 0 agreed, 1 cancelled, 2 refused.</summary>
    public uint Response { get; }

    public PortalException()
    {
    }

    public PortalException(uint response, string message)
        : base($"{message} (the portal answered {response})") => Response = response;

    public PortalException(string message)
        : base(message)
    {
    }

    public PortalException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
