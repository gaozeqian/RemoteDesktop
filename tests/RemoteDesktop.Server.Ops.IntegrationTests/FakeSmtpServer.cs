using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// Just enough SMTP to receive one message: greeting, EHLO, optional AUTH, one MAIL/RCPT/DATA, QUIT.
/// </summary>
/// <remarks>
/// Not a mock library: the console's mail path is a socket conversation, and the parts worth testing — the trailing
/// dot, the encoded subject, the AUTH exchange, the recipient list — only exist on the wire.
/// </remarks>
internal sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<string> _message = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _serving;

    /// <summary>The port the server listens on.</summary>
    public int Port { get; }

    /// <summary>Completed with the DATA block once a message arrives.</summary>
    public Task<string> Message => _message.Task;

    /// <summary>The AUTH token the client sent, if any.</summary>
    public string? AuthToken { get; private set; }

    /// <param name="requireAuth">Advertise AUTH and refuse MAIL until it happened.</param>
    public FakeSmtpServer(bool requireAuth = false)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _serving = Task.Run(() => ServeAsync(requireAuth));
    }

    private async Task ServeAsync(bool requireAuth)
    {
        using var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };

        await writer.WriteLineAsync("220 fake.test ESMTP").ConfigureAwait(false);

        var authenticated = !requireAuth;
        while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
        {
            var verb = line.Split(' ', 2)[0].ToUpperInvariant();
            switch (verb)
            {
                case "EHLO":
                case "HELO":
                    await writer.WriteLineAsync("250-fake.test").ConfigureAwait(false);
                    if (requireAuth)
                    {
                        await writer.WriteLineAsync("250-AUTH PLAIN LOGIN").ConfigureAwait(false);
                    }

                    await writer.WriteLineAsync("250 OK").ConfigureAwait(false);
                    break;

                case "AUTH":
                    AuthToken = line.Length > 11 ? line[11..] : string.Empty;
                    authenticated = true;
                    await writer.WriteLineAsync("235 authenticated").ConfigureAwait(false);
                    break;

                case "MAIL":
                    await writer.WriteLineAsync(authenticated ? "250 sender ok" : "530 authenticate first").ConfigureAwait(false);
                    break;

                case "RCPT":
                    await writer.WriteLineAsync("250 recipient ok").ConfigureAwait(false);
                    break;

                case "DATA":
                    await writer.WriteLineAsync("354 end with a dot").ConfigureAwait(false);
                    var body = new StringBuilder();
                    while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } dataLine)
                    {
                        if (dataLine == ".")
                        {
                            break;
                        }

                        body.Append(dataLine).Append("\r\n");
                    }

                    _message.TrySetResult(body.ToString());
                    await writer.WriteLineAsync("250 queued").ConfigureAwait(false);
                    break;

                case "QUIT":
                    await writer.WriteLineAsync("221 bye").ConfigureAwait(false);
                    return;

                default:
                    await writer.WriteLineAsync("250 ok").ConfigureAwait(false);
                    break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _serving.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // The client went away; nothing left to serve.
        }

        _stop.Dispose();
    }
}
