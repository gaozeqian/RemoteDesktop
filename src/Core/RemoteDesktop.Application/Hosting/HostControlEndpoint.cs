using System.Net.Sockets;
using System.Runtime.Versioning;

namespace RemoteDesktop.Application.Hosting;

/// <summary>
/// Where the UI and the background service meet to talk about this device, and how to open a stream to it.
/// </summary>
/// <remarks>
/// <para>
/// Two addresses, one shape. On Windows the configured name is a named pipe, and the service creates it with an ACL
/// that keeps the network out — that part needs the Windows platform project and stays in the host. Everywhere else
/// it is a Unix domain socket under the per-user runtime directory, which is reachable by that user and nobody else
/// by construction: no ACL to get right, and a different security story from the Windows side worth stating plainly.
/// On Windows any authenticated local user may ask the service for this device's code; here only the account that
/// owns the runtime directory can, which in practice is the same account the host already runs as.
/// </para>
/// <para>
/// A configured name that is already a rooted path is used as it is, so <c>--HostControlPipe /tmp/dev.sock</c> keeps
/// its meaning. The .NET named pipe implementation puts its sockets in a shared temporary directory that the first
/// user to run wins, which is why this does not simply rely on it.
/// </para>
/// </remarks>
public static class HostControlEndpoint
{
    /// <summary>Turns a configured name into the address both sides meet at.</summary>
    public static string Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (OperatingSystem.IsWindows() || Path.IsPathRooted(name))
        {
            return name;
        }

        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var directory = string.IsNullOrEmpty(runtime)
            ? Path.Combine(Path.GetTempPath(), $"remotedesktop-{Environment.UserName}")
            : Path.Combine(runtime, "remotedesktop");

        return Path.Combine(directory, name + ".sock");
    }

    /// <summary>Opens a connection to an endpoint that is already listening.</summary>
    /// <exception cref="IOException">Nothing is listening, or the address is not usable.</exception>
    /// <exception cref="TimeoutException">Nothing answered in time.</exception>
    public static async Task<Stream> ConnectAsync(string address, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        if (OperatingSystem.IsWindows() && Path.IsPathRooted(address) is false)
        {
            // Identification: the server may learn who is calling but cannot impersonate this user, which is what a
            // process that got hold of the pipe name before the service would otherwise be able to do.
            var pipe = new System.IO.Pipes.NamedPipeClientStream(
                ".",
                address,
                System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous,
                System.Security.Principal.TokenImpersonationLevel.Identification);
            try
            {
                await pipe.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(address), cancellationToken)
                .AsTask()
                .WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
            socket.Dispose();

            // Callers look for IOException when nothing is listening, and the socket errors that mean that — no such
            // file, connection refused — are neither IOExceptions nor agreed on across platforms.
            throw ex switch
            {
                TimeoutException => new TimeoutException($"Nothing answered at {address}.", ex),
                SocketException { SocketErrorCode: SocketError.TimedOut } => new TimeoutException($"Nothing answered at {address}.", ex),
                _ => new IOException($"Nothing is listening at {address}.", ex),
            };
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Creates the directory a socket address lives in, reachable only by its owner.</summary>
    [UnsupportedOSPlatform("windows")]
    public static void EnsureSocketDirectory(string address)
    {
        if (Path.GetDirectoryName(address) is not { Length: > 0 } directory)
        {
            return;
        }

        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(directory, mode);

        // CreateDirectory's mode applies only when it creates the directory; an existing one keeps whatever mode it
        // had. That matters for the /tmp fallback, where another local user can create the name first: the mode would
        // stay theirs, and the socket (and the fixed password typed into it) would be theirs too. Setting the mode
        // fails unless this process owns the directory, so the host stops instead of using it. A link is refused for
        // the same reason - the directory that would be protected and the one actually used would differ.
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"The control-socket directory '{directory}' is a symbolic link.");
        }

        File.SetUnixFileMode(directory, mode);
    }
}

/// <summary>
/// Listens for one connection at a time on a Unix domain socket. The Windows equivalent creates the pipe with an ACL
/// and lives in the host, because that needs the Windows platform project.
/// </summary>
public sealed class HostControlSocketListener : IAsyncDisposable
{
    private readonly Socket _listener;
    private readonly string _address;

    [UnsupportedOSPlatform("windows")]
    public HostControlSocketListener(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        _address = address;
        HostControlEndpoint.EnsureSocketDirectory(address);

        // A socket file left behind by a run that did not exit cleanly would make Bind fail; nobody can be listening
        // on it, because a second instance of this host is not allowed to reach this point.
        File.Delete(address);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(address));
        _listener.Listen(backlog: 4);
    }

    public async ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        var socket = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
    }

    public ValueTask DisposeAsync()
    {
        _listener.Dispose();
        File.Delete(_address);
        return ValueTask.CompletedTask;
    }
}
