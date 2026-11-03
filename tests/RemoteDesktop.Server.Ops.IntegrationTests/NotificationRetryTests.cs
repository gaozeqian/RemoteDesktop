using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// A webhook that is down when an alert opens must not lose that page. The first delivery fails, the next evaluation
/// after the backoff sends it again.
/// </summary>
[Collection("ops-console")]
public class NotificationRetryTests(OpsHostFixture fixture) : IClassFixture<OpsHostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_failed_open_notification_is_retried_after_the_backoff()
    {
        var webhook = WebApplication.CreateSlimBuilder();
        webhook.WebHost.UseUrls("http://127.0.0.1:0");
        webhook.Logging.ClearProviders();
        var hook = webhook.Build();

        // A name of its own, and only requests that carry it are counted. The other test in this class leaves an alert
        // behind whose rule and node it deleted; the next evaluation pass resolves that orphan and posts a "resolved"
        // notification to whatever webhook is configured by then. Which test runs first depends on the platform, so
        // counting every request made this one pass on Linux and fail on macOS.
        var ruleName = "retry-" + Guid.NewGuid().ToString("N")[..12];
        var calls = 0;
        var failuresRemaining = 1;
        hook.MapPost("/hook", async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(Ct);
            if (!body.Contains(ruleName, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                await context.Response.CompleteAsync();
                return;
            }

            Interlocked.Increment(ref calls);
            context.Response.StatusCode = Interlocked.Decrement(ref failuresRemaining) >= 0
                ? StatusCodes.Status500InternalServerError
                : StatusCodes.Status204NoContent;
            await context.Response.CompleteAsync();
        });

        await hook.StartAsync(Ct);
        var address = hook.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        var options = fixture.Services.GetRequiredService<OpsOptions>();
        var previousUrl = options.Notifications.WebhookUrl;
        options.Notifications.WebhookUrl = address.TrimEnd('/') + "/hook";

        var (client, cookies) = await SignInAsync();
        var store = fixture.Services.GetRequiredService<OpsStore>();
        string? ruleId = null;
        string? nodeId = null;
        var otherScrapeRules = Array.Empty<AlertRule>();

        try
        {
            var csrf = cookies.GetCookies(fixture.BaseAddress)["ops_csrf"]!.Value;
            ruleId = await CreateRuleAsync(client, csrf, ruleName);
            nodeId = await CreateFailingNodeAsync(client, csrf);

            // The fixture seeds a scrape rule. It would open a second alert for the same failing node and make the
            // call count ambiguous; this test is about the retry of one notification.
            otherScrapeRules = (await store.ListAlertRulesAsync(Ct))
                .Where(rule => rule.Id != ruleId && rule.Kind == AlertRuleKinds.Scrape && rule.Enabled)
                .ToArray();
            foreach (var rule in otherScrapeRules)
            {
                await store.UpsertAlertRuleAsync(rule with { Enabled = false }, Ct);
            }

            await fixture.Services.GetRequiredService<MetricScraper>().RunOnceAsync(Ct);
            var evaluation = fixture.Services.GetRequiredService<AlertEvaluationService>();

            await evaluation.RunOnceAsync(Ct);
            Assert.Equal(1, Volatile.Read(ref calls));

            // The open alert produces no new transition on the next pass; only the retry pass can deliver this.
            fixture.Time.Advance(TimeSpan.FromSeconds(61));
            await evaluation.RunOnceAsync(Ct);

            Assert.Equal(2, Volatile.Read(ref calls));
            Assert.Empty(await fixture.Services.GetRequiredService<OpsStore>().ListPendingNotificationsAsync(
                "webhook",
                NotificationBackoff.MaxAttempts,
                cancellationToken: Ct));
        }
        finally
        {
            options.Notifications.WebhookUrl = previousUrl;
            foreach (var rule in otherScrapeRules)
            {
                await store.UpsertAlertRuleAsync(rule with { Enabled = true }, Ct);
            }

            using (client)
            {
                var csrf = cookies.GetCookies(fixture.BaseAddress)["ops_csrf"]?.Value;
                if (nodeId is not null && csrf is not null)
                {
                    using var deleteNode = new HttpRequestMessage(HttpMethod.Delete, $"/api/nodes/{nodeId}");
                    deleteNode.Headers.Add("X-Ops-CSRF", csrf);
                    await client.SendAsync(deleteNode, Ct);
                }

                if (ruleId is not null && csrf is not null)
                {
                    using var deleteRule = new HttpRequestMessage(HttpMethod.Delete, $"/api/settings/rules/{ruleId}");
                    deleteRule.Headers.Add("X-Ops-CSRF", csrf);
                    await client.SendAsync(deleteRule, Ct);
                }
            }

            await hook.StopAsync(Ct);
            await hook.DisposeAsync();
        }
    }

    /// <summary>
    /// Two ways of reaching a person are two independent promises: the mail arrives while the webhook is unreachable,
    /// and each channel keeps its own attempt row — one being owed a retry says nothing about the other.
    /// </summary>
    [Fact]
    public async Task A_dead_webhook_does_not_stop_the_mail_and_the_two_keep_separate_retries()
    {
        await using var smtp = new FakeSmtpServer();

        var options = fixture.Services.GetRequiredService<OpsOptions>();
        var previousUrl = options.Notifications.WebhookUrl;
        var previousSmtp = options.Notifications.Smtp;
        options.Notifications.WebhookUrl = "http://127.0.0.1:1/hook";
        options.Notifications.Smtp = new SmtpSettings
        {
            Host = "127.0.0.1",
            Port = smtp.Port,
            UseStartTls = false,
            From = "ops@example.com",
            To = "ops@example.com",
        };

        var store = fixture.Services.GetRequiredService<OpsStore>();
        var (client, cookies) = await SignInAsync();
        string? ruleId = null;
        string? nodeId = null;
        var otherScrapeRules = Array.Empty<AlertRule>();

        try
        {
            var csrf = cookies.GetCookies(fixture.BaseAddress)["ops_csrf"]!.Value;
            ruleId = await CreateRuleAsync(client, csrf);
            nodeId = await CreateFailingNodeAsync(client, csrf);

            otherScrapeRules = (await store.ListAlertRulesAsync(Ct))
                .Where(rule => rule.Id != ruleId && rule.Kind == AlertRuleKinds.Scrape && rule.Enabled)
                .ToArray();
            foreach (var rule in otherScrapeRules)
            {
                await store.UpsertAlertRuleAsync(rule with { Enabled = false }, Ct);
            }

            await fixture.Services.GetRequiredService<MetricScraper>().RunOnceAsync(Ct);
            await fixture.Services.GetRequiredService<AlertEvaluationService>().RunOnceAsync(Ct);

            // The mail went out even though the webhook could not be reached.
            var message = await smtp.Message.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Contains("测试：重试通知", message, StringComparison.Ordinal);

            var alert = Assert.Single(await store.ListOpenAlertsAsync(Ct), row => row.RuleId == ruleId);
            Assert.Contains(
                await store.ListPendingNotificationsAsync("webhook", NotificationBackoff.MaxAttempts, cancellationToken: Ct),
                item => item.AlertId == alert.Id);
            Assert.DoesNotContain(
                await store.ListPendingNotificationsAsync("smtp", NotificationBackoff.MaxAttempts, cancellationToken: Ct),
                item => item.AlertId == alert.Id);
        }
        finally
        {
            options.Notifications.WebhookUrl = previousUrl;
            options.Notifications.Smtp = previousSmtp;
            foreach (var rule in otherScrapeRules)
            {
                await store.UpsertAlertRuleAsync(rule with { Enabled = true }, Ct);
            }

            using (client)
            {
                var csrf = cookies.GetCookies(fixture.BaseAddress)["ops_csrf"]?.Value;
                if (nodeId is not null && csrf is not null)
                {
                    using var deleteNode = new HttpRequestMessage(HttpMethod.Delete, $"/api/nodes/{nodeId}");
                    deleteNode.Headers.Add("X-OPS-CSRF", csrf);
                    await client.SendAsync(deleteNode, Ct);
                }

                if (ruleId is not null && csrf is not null)
                {
                    using var deleteRule = new HttpRequestMessage(HttpMethod.Delete, $"/api/settings/rules/{ruleId}");
                    deleteRule.Headers.Add("X-OPS-CSRF", csrf);
                    await client.SendAsync(deleteRule, Ct);
                }
            }
        }
    }

    private static async Task<string> CreateRuleAsync(HttpClient client, string csrf, string name = "测试：重试通知")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/settings/rules")
        {
            Content = JsonContent.Create(new
            {
                name,
                kind = "scrape",
                comparator = "gt",
                threshold = 0.5,
                forSeconds = 0,
                severity = "P2",
                enabled = true,
            }),
        };
        request.Headers.Add("X-Ops-CSRF", csrf);

        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rule = await response.Content.ReadFromJsonAsync<RuleDto>(Ct);
        return rule!.Id;
    }

    private static async Task<string> CreateFailingNodeAsync(HttpClient client, string csrf)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/nodes")
        {
            Content = JsonContent.Create(new
            {
                name = "notify-fail-" + Guid.NewGuid().ToString("N")[..8],
                role = "relay",
                baseUrl = "http://127.0.0.1:1",
            }),
        };
        request.Headers.Add("X-Ops-CSRF", csrf);

        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var node = await response.Content.ReadFromJsonAsync<NodeDto>(Ct);
        return node!.Id;
    }

    private async Task<(HttpClient Client, CookieContainer Cookies)> SignInAsync()
    {
        var (client, cookies) = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = OpsHostFixture.Password }, Ct);
        var challenge = (await login.Content.ReadFromJsonAsync<LoginResponse>(Ct))!.Challenge;

        var totp = await client.PostAsJsonAsync(
            "/api/auth/totp",
            new { challenge, code = fixture.NextTotp(fixture.AdminSecret) },
            Ct);
        Assert.Equal(HttpStatusCode.OK, totp.StatusCode);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (client, cookies);
    }

    public sealed record RuleDto(string Id);

    public sealed record NodeDto(string Id);

    public sealed record LoginResponse(string? Status, string? Challenge);
}
