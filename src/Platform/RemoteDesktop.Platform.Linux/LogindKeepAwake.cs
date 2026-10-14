using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Platform.Posix.Interop;
using Tmds.DBus.Protocol;

namespace RemoteDesktop.Platform.Linux;

/// <summary>
/// Keeps the machine awake while a controller is connected, by holding a logind inhibitor open.
/// </summary>
/// <remarks>
/// <para>
/// A logind inhibitor is a file descriptor: asking for one starts the inhibition, closing it ends it, and there is no
/// separate call to make. That shape is why this class keeps a descriptor rather than a flag, and why the descriptor
/// has to be duplicated out of the reply — the connection owns the one it hands over and closes it as soon as the
/// reply has been read, which would release the inhibitor immediately.
/// </para>
/// <para>
/// The calls block. <see cref="ISystemKeepAwake"/> is synchronous and these happen once when a session starts and once
/// when it ends, so the round trip costs nothing worth this being asynchronous for. That reasoning is what would have
/// to change if this were ever called per frame or from a D-Bus callback: blocking a thread that the connection
/// itself needs would deadlock.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class LogindKeepAwake(ILogger<LogindKeepAwake> logger) : ISystemKeepAwake, IDisposable
{
    private const string Destination = "org.freedesktop.login1";
    private const string ObjectPath = "/org/freedesktop/login1";
    private const string Manager = "org.freedesktop.login1.Manager";

    // "sleep" covers suspend; "idle" covers the desktop's own idle handling. Both are wanted.
    private const string What = "idle:sleep";
    private const string Who = "Remote Desktop";
    private const string Why = "A controller is connected.";

    private readonly Lock _gate = new();

    private DBusConnection? _connection;
    private SafeFileHandle? _inhibitor;
    private int _count;
    private bool _disposed;

    public void Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _count++;
            if (_count > 1 || _inhibitor is not null)
            {
                return;
            }

            if (!TryInhibit(out var failure))
            {
                // Not being able to inhibit is not a reason to refuse a session: the machine may simply not run
                // logind, and the session works without it — it just may fall asleep in the middle of one. Said out
                // loud because that is otherwise a mystery a long time after the fact.
                LogCannotStayAwake(logger, failure);
            }
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                return;
            }

            if (--_count > 0)
            {
                return;
            }

            // Closing the descriptor is what ends the inhibition.
            _inhibitor?.Dispose();
            _inhibitor = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _count = 0;
            _inhibitor?.Dispose();
            _inhibitor = null;
            _connection?.Dispose();
            _connection = null;
        }
    }

    /// <returns><c>false</c> when no inhibitor could be taken, with <paramref name="failure"/> saying why.</returns>
    private bool TryInhibit(out string failure)
    {
        try
        {
            var connection = SystemBus();
            var handle = Inhibit(connection, "block");
            _connection = connection;
            _inhibitor = handle;
            failure = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or TimeoutException)
        {
            failure = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Takes an inhibitor as a file descriptor the caller owns. The descriptor is duplicated because the one in the
    /// reply dies with the reply.
    /// </summary>
    private static SafeFileHandle Inhibit(DBusConnection connection, string mode)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Destination, ObjectPath, Manager, "Inhibit", "ssss", MessageFlags.None);
        writer.WriteString(What);
        writer.WriteString(Who);
        writer.WriteString(Why);
        writer.WriteString(mode);

        var duplicated = connection
            .CallMethodAsync(writer.CreateMessage(), static (Message message, object? _) =>
            {
                var raw = message.GetBodyReader().ReadHandleRaw();
                var copy = LibC.DuplicateFileDescriptor((int)raw);
                if (copy < 0)
                {
                    throw new IOException($"Could not duplicate the inhibitor descriptor (errno {Marshal.GetLastPInvokeError()}).");
                }

                return copy;
            }, null)
            .GetAwaiter()
            .GetResult();

        return new SafeFileHandle(duplicated, ownsHandle: true);
    }

    private DBusConnection SystemBus()
    {
        if (_connection is not null)
        {
            return _connection;
        }

        // Null when DBUS_SYSTEM_BUS_ADDRESS and the default socket are both absent, which happens in containers and
        // in a session started without a system bus. The message says which bus was missing rather than surfacing as
        // an ArgumentNullException from somewhere inside the library.
        var address = DBusAddress.System
            ?? throw new IOException("There is no system bus to ask; the machine cannot be kept awake.");
        var connection = new DBusConnection(address);
        // ConnectAsync is a ValueTask, which must not be blocked on directly (CA2012): unlike a Task there is no
        // promise that waiting on it actually waits.
        connection.ConnectAsync().AsTask().GetAwaiter().GetResult();
        return connection;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not stop the machine from sleeping; a session may be interrupted if it idles. Reason: {Reason}")]
    private static partial void LogCannotStayAwake(ILogger logger, string reason);
}
