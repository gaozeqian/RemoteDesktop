using System.Globalization;
using System.Text;
using System.Text.Json;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops;

/// <summary>One way an alert reaches a person.</summary>
/// <remarks>
/// The channels are independent by design: each keeps its own row in <c>ops_notification_attempt</c>, so a webhook that
/// is down neither suppresses the mail nor is retried because of it.
/// </remarks>
public interface INotificationChannel
{
    /// <summary>What the attempt row records, and what the retry pass searches for.</summary>
    string Id { get; }

    /// <summary>Whether the deployment configured this channel at all.</summary>
    bool IsConfigured { get; }

    /// <returns><c>null</c> when it was delivered, otherwise the reason to show and to retry with.</returns>
    Task<string?> SendAsync(
        OpsAlertRow alert,
        string ruleName,
        string severity,
        string transition,
        CancellationToken cancellationToken);
}

/// <summary>The words every channel shares, so a page and a mail say the same thing about the same alert.</summary>
internal static class AlertNotification
{
    public static string Title(string severity, string ruleName, string transition) =>
        transition == "opened" ? $"[{severity}] {ruleName}" : $"[已恢复] {ruleName}";

    public static string Body(OpsAlertRow alert, string ruleName, string severity, string transition, DateTimeOffset now)
    {
        var resolvedAt = transition == "resolved" ? alert.ResolvedAt ?? now.ToUnixTimeMilliseconds() : (long?)null;
        var text = new StringBuilder();
        text.Append("规则: ").Append(ruleName).Append('\n');
        text.Append("状态: ").Append(transition == "opened" ? "打开" : "已恢复").Append('\n');
        text.Append("级别: ").Append(severity).Append('\n');

        if (alert.Value is { } value)
        {
            text.Append("读数: ").Append(value.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
        }

        text.Append("告警 id: ").Append(alert.Id).Append('\n');
        text.Append("去重键: ").Append(alert.DedupeKey).Append('\n');
        text.Append("打开时间: ").Append(Iso(alert.OpenedAt)).Append('\n');
        if (resolvedAt is { } resolved)
        {
            text.Append("恢复时间: ").Append(Iso(resolved)).Append('\n');
        }

        text.Append('\n').Append("这台控制台由 Remote Desktop 运维监控台发送。").Append('\n');
        return text.ToString();
    }

    private static string Iso(long unixMilliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).UtcDateTime.ToString("u", CultureInfo.InvariantCulture);
}

/// <summary>The JSON POST the console has always sent: unchanged, including its field names.</summary>
internal sealed partial class WebhookNotificationChannel(
    OpsOptions options,
    IHttpClientFactory httpClientFactory,
    TimeProvider time,
    ILogger<WebhookNotificationChannel> logger) : INotificationChannel
{
    /// <summary>Named client, so a slow receiver cannot hold an evaluation open indefinitely.</summary>
    public const string ClientName = "ops-webhook";

    public const string ChannelId = "webhook";

    public string Id => ChannelId;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Notifications.WebhookUrl);

    public async Task<string?> SendAsync(
        OpsAlertRow alert,
        string ruleName,
        string severity,
        string transition,
        CancellationToken cancellationToken)
    {
        var url = options.Notifications.WebhookUrl;
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();

        // The URL is checked at start-up, but this method also runs from the retry pass and must not let a malformed
        // value thrown from Uri's constructor escape into the background loop.
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var webhook)
            || (webhook.Scheme != Uri.UriSchemeHttp && webhook.Scheme != Uri.UriSchemeHttps))
        {
            NotifyFailed(logger, ruleName, "Ops:Notifications:WebhookUrl is not an absolute http(s) URL");
            return "invalid webhook URL";
        }

        try
        {
            using var client = httpClientFactory.CreateClient(ClientName);
            var payload = JsonSerializer.Serialize(
                new
                {
                    @event = transition,
                    title = AlertNotification.Title(severity, ruleName, transition),
                    alert = new
                    {
                        id = alert.Id,
                        rule = ruleName,
                        severity,
                        value = alert.Value,
                        openedAt = alert.OpenedAt,
                        resolvedAt = transition == "resolved" ? alert.ResolvedAt ?? now : (long?)null,
                    },
                },
                OpsEventJson.Options);

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(webhook, content, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            NotifyRejected(logger, ruleName, (int)response.StatusCode);
            return $"HTTP {(int)response.StatusCode}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            NotifyFailed(logger, ruleName, "timed out");
            return "timed out";
        }
        catch (Exception ex)
        {
            // A DNS failure, a TLS error, a response the handler could not parse: none of these may reach the
            // evaluation loop, and all of them are worth another attempt later.
            NotifyFailed(logger, ruleName, ex.Message);
            return ex.Message;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The webhook for rule {Rule} answered HTTP {Status}.")]
    private static partial void NotifyRejected(ILogger logger, string rule, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The webhook for rule {Rule} failed: {Reason}.")]
    private static partial void NotifyFailed(ILogger logger, string rule, string reason);
}

/// <summary>The same alert, delivered as a plain-text mail.</summary>
internal sealed partial class SmtpNotificationChannel(
    OpsOptions options,
    TimeProvider time,
    ILogger<SmtpNotificationChannel> logger) : INotificationChannel
{
    public const string ChannelId = "smtp";

    public string Id => ChannelId;

    public bool IsConfigured => options.Notifications.Smtp.IsConfigured;

    public async Task<string?> SendAsync(
        OpsAlertRow alert,
        string ruleName,
        string severity,
        string transition,
        CancellationToken cancellationToken)
    {
        var settings = options.Notifications.Smtp;
        var subject = AlertNotification.Title(severity, ruleName, transition);
        var body = AlertNotification.Body(alert, ruleName, severity, transition, time.GetUtcNow());

        try
        {
            await SmtpMailer.SendAsync(settings, subject, body, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The reason is stored on the attempt row and logged; the settings (and the password) never are.
            NotifyFailed(logger, ruleName, ex.Message);
            return ex.Message;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The mail for rule {Rule} was not delivered: {Reason}")]
    private static partial void NotifyFailed(ILogger logger, string rule, string reason);
}
