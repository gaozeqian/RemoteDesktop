using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace RemoteDesktop.Server.Ops;

/// <summary>Raised when a mail server refuses a step of a delivery.</summary>
internal sealed class SmtpDeliveryException(string message) : Exception(message);

/// <summary>
/// The smallest SMTP client that delivers one alert: connect, EHLO, optional STARTTLS, optional AUTH, one message.
/// </summary>
/// <remarks>
/// Written rather than taken from a package on purpose. The console needs six commands of the protocol, the BCL's
/// <c>System.Net.Mail.SmtpClient</c> is the one type Microsoft says not to use for new code, and a dependency would
/// mean a third-party notice, a version to track and a second TLS stack to reason about. What this deliberately does
/// not do: implicit TLS on port 465, DSN/ETRN, pipelining. A relay that needs 465 is a deployment that should put a
/// local relay in front instead.
/// </remarks>
internal static class SmtpMailer
{
    /// <summary>One delivery, whole connection included. An alert page that takes longer than this is not a page.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>No SMTP reply is anything like this long; a server that sends one is not speaking SMTP.</summary>
    private const int MaxReplyLength = 8 * 1024;

    /// <exception cref="SmtpDeliveryException">The server refused a step, or could not be reached.</exception>
    public static async Task SendAsync(
        SmtpSettings settings, string subject, string text, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(text);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var limit = timeout ?? Timeout;
        if (limit <= TimeSpan.Zero)
        {
            // CancelAfter(0) still goes through a timer; a budget that is already spent should fail deterministically.
            stop.Cancel();
        }
        else
        {
            stop.CancelAfter(limit);
        }

        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(settings.Host, settings.Port, stop.Token).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            throw new SmtpDeliveryException($"cannot reach {settings.Host}:{settings.Port}: {ex.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own deadline fired while connecting; without this the failure would read "A task was canceled.".
            throw new SmtpDeliveryException($"timed out connecting to {settings.Host}:{settings.Port}");
        }

        Stream stream = tcp.GetStream();
        try
        {
            await ExpectAsync(stream, 220, "the greeting", stop.Token).ConfigureAwait(false);

            var capabilities = await HelloAsync(stream, stop.Token).ConfigureAwait(false);

            if (settings.UseStartTls)
            {
                if (!capabilities.Any(capability => capability.StartsWith("STARTTLS", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new SmtpDeliveryException(
                        $"{settings.Host} does not offer STARTTLS; set Ops:Notifications:Smtp:UseStartTls to false only on a trusted network");
                }

                await CommandAsync(stream, "STARTTLS", stop.Token).ConfigureAwait(false);
                await ExpectAsync(stream, 220, "STARTTLS", stop.Token).ConfigureAwait(false);

                var secure = new SslStream(stream, leaveInnerStreamOpen: false);
                await secure
                    .AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = settings.Host }, stop.Token)
                    .ConfigureAwait(false);
                stream = secure;

                // Capabilities after the upgrade are the ones that count: a server may only offer AUTH inside TLS.
                capabilities = await HelloAsync(stream, stop.Token).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(settings.Username))
            {
                await AuthenticateAsync(stream, capabilities, settings, stop.Token).ConfigureAwait(false);
            }

            await CommandAsync(stream, $"MAIL FROM:<{settings.From}>", stop.Token).ConfigureAwait(false);
            await ExpectAsync(stream, 250, "MAIL FROM", stop.Token).ConfigureAwait(false);

            foreach (var recipient in settings.Recipients())
            {
                await CommandAsync(stream, $"RCPT TO:<{recipient}>", stop.Token).ConfigureAwait(false);
                await ExpectAsync(stream, 250, "RCPT TO", stop.Token).ConfigureAwait(false);
            }

            await CommandAsync(stream, "DATA", stop.Token).ConfigureAwait(false);
            await ExpectAsync(stream, 354, "DATA", stop.Token).ConfigureAwait(false);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(Message(settings, subject, text)), stop.Token).ConfigureAwait(false);
            await CommandAsync(stream, ".", stop.Token).ConfigureAwait(false);
            await ExpectAsync(stream, 250, "the message", stop.Token).ConfigureAwait(false);

            // Accepted at this point. A relay that closes without answering QUIT has still taken the message.
            await CommandAsync(stream, "QUIT", stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new SmtpDeliveryException($"timed out talking to {settings.Host}:{settings.Port}");
        }
        catch (IOException ex)
        {
            throw new SmtpDeliveryException($"the connection to {settings.Host}:{settings.Port} failed: {ex.Message}");
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<List<string>> HelloAsync(Stream stream, CancellationToken cancellationToken)
    {
        await CommandAsync(stream, "EHLO " + Environment.MachineName, cancellationToken).ConfigureAwait(false);
        var reply = await ReadReplyAsync(stream, cancellationToken).ConfigureAwait(false);
        if (reply.Code != 250)
        {
            throw new SmtpDeliveryException($"the server refused EHLO: {Describe(reply)}");
        }

        // Each line carries its own "250-"/"250 " prefix; the capability is what follows it.
        return [.. reply.Lines.Select(line => line.Length > 4 ? line[4..] : string.Empty)];
    }

    private static async Task AuthenticateAsync(
        Stream stream,
        List<string> capabilities,
        SmtpSettings settings,
        CancellationToken cancellationToken)
    {
        var mechanisms = capabilities
            .Where(line => line.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase))
            .SelectMany(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToArray();

        if (mechanisms.Contains("PLAIN", StringComparer.OrdinalIgnoreCase))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{settings.Username}\0{settings.Password}"));
            await CommandAsync(stream, "AUTH PLAIN " + token, cancellationToken).ConfigureAwait(false);
            await ExpectAsync(stream, 235, "AUTH PLAIN", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!mechanisms.Contains("LOGIN", StringComparer.OrdinalIgnoreCase))
        {
            throw new SmtpDeliveryException(
                "the server offers neither AUTH PLAIN nor AUTH LOGIN; remove Ops:Notifications:Smtp:Username or use a relay that supports one");
        }

        await CommandAsync(stream, "AUTH LOGIN", cancellationToken).ConfigureAwait(false);
        await ExpectAsync(stream, 334, "AUTH LOGIN", cancellationToken).ConfigureAwait(false);
        await CommandAsync(stream, Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.Username)), cancellationToken).ConfigureAwait(false);
        await ExpectAsync(stream, 334, "the user name", cancellationToken).ConfigureAwait(false);
        await CommandAsync(stream, Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.Password)), cancellationToken).ConfigureAwait(false);
        await ExpectAsync(stream, 235, "the password", cancellationToken).ConfigureAwait(false);
    }

    private static async Task CommandAsync(Stream stream, string command, CancellationToken cancellationToken) =>
        await stream.WriteAsync(Encoding.UTF8.GetBytes(command + "\r\n"), cancellationToken).ConfigureAwait(false);

    /// <summary>Reads one reply, following the <c>250-</c> continuation lines a multi-line reply uses.</summary>
    private static async Task<(int Code, List<string> Lines)> ReadReplyAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var bytes = new List<byte>();
        var single = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(single, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new SmtpDeliveryException("the mail server closed the connection");
            }

            var b = single[0];
            if (b != (byte)'\n')
            {
                if (bytes.Count >= MaxReplyLength)
                {
                    throw new SmtpDeliveryException("the mail server sent a reply longer than any SMTP reply");
                }

                bytes.Add(b);
                continue;
            }

            var line = Encoding.UTF8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes)).TrimEnd('\r');
            bytes.Clear();
            lines.Add(line);

            // "250-text" continues, "250 text" ends. A line too short to carry a code ends the reply as well.
            if (line.Length < 4 || line[3] != '-')
            {
                break;
            }
        }

        var first = lines[0];
        if (first.Length < 3
            || !int.TryParse(first.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var code))
        {
            throw new SmtpDeliveryException($"the mail server answered with something that is not an SMTP reply: {first}");
        }

        return (code, lines);
    }

    private static async Task ExpectAsync(Stream stream, int expected, string step, CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(stream, cancellationToken).ConfigureAwait(false);
        if (reply.Code != expected)
        {
            throw new SmtpDeliveryException($"the server answered {reply.Code} at {step} (expected {expected}): {Describe(reply)}");
        }
    }

    private static string Describe((int Code, List<string> Lines) reply)
    {
        var text = string.Join(" / ", reply.Lines);
        return text.Length <= 200 ? text : text[..200];
    }

    private static string Message(SmtpSettings settings, string subject, string text)
    {
        var message = new StringBuilder();
        message.Append("From: ").Append(settings.From).Append("\r\n");
        message.Append("To: ").Append(string.Join(", ", settings.Recipients())).Append("\r\n");
        message.Append("Subject: ").Append(EncodeHeader(subject)).Append("\r\n");
        message.Append("Date: ").Append(DateTimeOffset.UtcNow.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
        message.Append("MIME-Version: 1.0\r\n");
        message.Append("Content-Type: text/plain; charset=utf-8\r\n");
        message.Append("Content-Transfer-Encoding: 8bit\r\n");
        message.Append("\r\n");

        // A line that starts with a dot would end the DATA block early, so SMTP doubles it. The terminating dot is
        // sent by the caller after this, which is why the body ends with a CRLF of its own.
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            message.Append(line.StartsWith('.') ? "." : string.Empty).Append(line).Append("\r\n");
        }

        return message.ToString();
    }

    /// <summary>
    /// RFC 2047 for anything that is not printable ASCII — which also means a rule name carrying CR or LF becomes an
    /// encoded word instead of a second header line.
    /// </summary>
    private static string EncodeHeader(string value)
    {
        if (value.All(character => character is >= ' ' and <= '~'))
        {
            return value;
        }

        return "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "?=";
    }
}
