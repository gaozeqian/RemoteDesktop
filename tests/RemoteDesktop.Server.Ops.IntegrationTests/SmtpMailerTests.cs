using System.Text;
using RemoteDesktop.Server.Ops;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// The console's own SMTP client. These tests talk to a socket rather than a mock, because that is where the mistakes
/// live: a missing trailing dot, an unencoded subject, a credential sent to a server that never asked for one.
/// </summary>
public sealed class SmtpMailerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_message_arrives_with_its_recipients_and_a_readable_body()
    {
        await using var server = new FakeSmtpServer();
        var settings = Settings(server.Port, username: null);

        await SmtpMailer.SendAsync(settings, "[P1] 节点抓取失败", "规则: 节点抓取失败\n状态: 打开", Ct);

        var message = await server.Message.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Contains("To: ops@example.com", message, StringComparison.Ordinal);
        Assert.Contains("规则: 节点抓取失败", message, StringComparison.Ordinal);

        // A subject with Chinese in it cannot travel as plain ASCII; RFC 2047 is what makes the reader see the rule.
        Assert.Contains("Subject: =?UTF-8?B?", message, StringComparison.Ordinal);

        // No credentials configured, so none were offered.
        Assert.Null(server.AuthToken);
    }

    /// <summary>A body line that starts with a dot would otherwise end the DATA block early.</summary>
    [Fact]
    public async Task A_line_that_looks_like_the_end_of_data_is_escaped()
    {
        await using var server = new FakeSmtpServer();

        await SmtpMailer.SendAsync(Settings(server.Port, username: null), "subject", "first\n.hidden", Ct);

        var message = await server.Message.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Contains("\r\n..hidden\r\n", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Credentials_travel_only_when_the_relay_asks_for_them()
    {
        await using var server = new FakeSmtpServer(requireAuth: true);

        await SmtpMailer.SendAsync(Settings(server.Port, "ops", "secret"), "subject", "body", Ct);

        await server.Message.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.NotNull(server.AuthToken);

        // AUTH PLAIN is \0user\0password, base64: what a server expects and what must never appear in a log.
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(server.AuthToken!));
        Assert.Equal("\0ops\0secret", decoded);
    }

    /// <summary>A relay that is not there has to read as a delivery failure, not as an unhandled socket error.</summary>
    [Fact]
    public async Task An_unreachable_server_is_reported_as_a_delivery_failure()
    {
        var ex = await Assert.ThrowsAsync<SmtpDeliveryException>(() =>
            SmtpMailer.SendAsync(Settings(1, username: null), "subject", "body", Ct));

        Assert.Contains("cannot reach 127.0.0.1:1", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>An expired connect deadline must read as a timeout, not as the runtime's "A task was canceled.".</summary>
    [Fact]
    public async Task A_connect_that_runs_out_of_time_is_reported_as_a_timeout()
    {
        // A zero deadline is already over when the connect starts, which is deterministic where an unroutable address is not.
        // Nothing needs to be listening: the connect is abandoned before it is attempted.
        var ex = await Assert.ThrowsAsync<SmtpDeliveryException>(() =>
            SmtpMailer.SendAsync(Settings(1, username: null), "subject", "body", Ct, TimeSpan.Zero));

        Assert.Contains("timed out", ex.Message, StringComparison.Ordinal);
    }

    private static SmtpSettings Settings(int port, string? username, string? password = null) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        UseStartTls = false,
        From = "ops@example.com",
        To = "ops@example.com",
        Username = username ?? string.Empty,
        Password = password ?? string.Empty,
    };
}
