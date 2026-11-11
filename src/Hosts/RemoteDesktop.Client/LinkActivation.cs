using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Application.Links;

namespace RemoteDesktop.Client;

/// <summary>
/// Hands a <c>remotedesktop://</c> link to the client that is already running.
/// </summary>
/// <remarks>
/// <para>
/// Opening a link starts the program, and a second client on the same machine registers the same controller identity
/// at the rendezvous server — which keeps one session per device, so the two processes would evict each other every
/// second. The first client therefore owns a named pipe; a later launch that carries a link writes the URL into that
/// pipe and exits. A launch without a link behaves exactly as it always has and simply loses the race for the pipe, so
/// links keep going to the first window.
/// </para>
/// <para>
/// The pipe is per user (<see cref="PipeOptions.CurrentUserOnly"/>, and the name is derived from the user's data
/// directory), because two people signed in to the same machine have two clients and two device identities.
/// </para>
/// </remarks>
internal static class LinkActivation
{
    private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The pipe the running client listens on.</summary>
    public static string PipeName { get; } = BuildPipeName();

    /// <summary>Offers a link to the running client, if there is one that took the pipe.</summary>
    /// <param name="link">The link that was opened.</param>
    /// <param name="pipeName">The pipe to use; the default is the one this machine's client listens on.</param>
    /// <returns><c>true</c> when somebody accepted it; the caller then exits instead of opening a second window.</returns>
    public static bool TryForward(ConnectionLink link, string? pipeName = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)ForwardTimeout.TotalMilliseconds);

            // The receiver runs the same parser as the command line, so the shape stays in one place.
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine($"remotedesktop://connect?code={link.Device}");
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentOutOfRangeException)
        {
            // No running client, a client that is starting, or a pipe this account may not use: all of them mean
            // "start normally" — two windows are better than a launch that silently does nothing. The last one is a
            // name the platform cannot express: on macOS and Linux a pipe is a socket in the temp directory, whose
            // path has a hard length limit, and a name past it is refused before any connection is attempted.
            return false;
        }
    }

    /// <summary>
    /// Starts the listener on the client that wins the pipe, and returns <c>null</c> on every other instance.
    /// </summary>
    /// <param name="onLink">Called on a background thread for each link that arrives.</param>
    /// <param name="pipeName">The pipe to listen on; the default is the one this machine's client uses.</param>
    public static IDisposable? TryListen(Action<ConnectionLink> onLink, string? pipeName = null)
    {
        ArgumentNullException.ThrowIfNull(onLink);

        var name = pipeName ?? PipeName;
        Mutex? owner;
        try
        {
            owner = new Mutex(initiallyOwned: true, name, out var created);
            if (!created)
            {
                owner.Dispose();
                return null;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return null;
        }

        var stop = new CancellationTokenSource();
        var listening = Task.Run(() => ListenAsync(name, onLink, stop.Token));
        return new Listener(owner, stop, listening);
    }

    private static async Task ListenAsync(string pipeName, Action<ConnectionLink> onLink, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // One instance at a time: connections are accepted one after another, and the name has to work on
                // every platform (MaxAllowedServerInstances is Windows-only).
                await using var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var text = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (ConnectionLink.TryParse(text, out var link))
                {
                    onLink(link);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentOutOfRangeException)
            {
                // The pipe cannot be served on this machine (an unsupported filesystem, a policy, or a name whose
                // socket path is too long here): stop trying rather than spin. Links then start a second window,
                // which is the behaviour without this class at all.
                break;
            }
        }
    }

    private static string BuildPipeName()
    {
        // A stable per-user name: the data directory is the one thing that already differs between the two accounts
        // that can be signed in at once, and a hash keeps the name short enough for the Unix socket path limit.
        var identity = DataDirectory.ForUser();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToUpperInvariant()));
        return $"RemoteDesktop.Client.Link.{Convert.ToHexString(hash.AsSpan(0, 8))}";
    }

    private sealed class Listener(Mutex owner, CancellationTokenSource stop, Task listening) : IDisposable
    {
        public void Dispose()
        {
            stop.Cancel();
            try
            {
                listening.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
                // The listener only ends by cancellation; a failure there is not this method's problem.
            }

            stop.Dispose();
            owner.Dispose();
        }
    }
}
