using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// A silence stops the pages, not the history: the alert still opens and is still audited, and no notification attempt
/// is recorded for it — so nothing is delivered once the window closes either.
/// </summary>
[Collection("ops-console")]
public class SilenceTests(OpsHostFixture fixture) : IClassFixture<OpsHostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_silenced_rule_opens_the_alert_but_sends_nothing()
    {
        // A mail server that would accept anything: the assertion below is "no attempt was recorded", which only
        // means something because this channel is configured and working.
        await using var smtp = new FakeSmtpServer();

        var options = fixture.Services.GetRequiredService<OpsOptions>();
        var previousUrl = options.Notifications.WebhookUrl;
        var previousSmtp = options.Notifications.Smtp;
        options.Notifications.WebhookUrl = string.Empty;
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
        string? silenceId = null;
        string? createdSilenceId = null;
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

            using (var create = new HttpRequestMessage(HttpMethod.Post, "/api/silences")
            {
                Content = JsonContent.Create(new { ruleId, hours = 1, reason = "测试静默" }),
            })
            {
                create.Headers.Add("X-Ops-CSRF", csrf);
                var created = await client.SendAsync(create, Ct);
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                createdSilenceId = (await created.Content.ReadFromJsonAsync<SilenceDto>(Ct))!.Id;
                silenceId = createdSilenceId;
            }

            await fixture.Services.GetRequiredService<MetricScraper>().RunOnceAsync(Ct);
            await fixture.Services.GetRequiredService<AlertEvaluationService>().RunOnceAsync(Ct);

            var alert = Assert.Single(await store.ListOpenAlertsAsync(Ct), row => row.RuleId == ruleId);
            Assert.False(await store.HasNotificationAttemptAsync(alert.Id, SmtpNotificationChannel.ChannelId, "opened", Ct));
            Assert.DoesNotContain(
                await store.ListPendingNotificationsAsync(SmtpNotificationChannel.ChannelId, NotificationBackoff.MaxAttempts, cancellationToken: Ct),
                item => item.AlertId == alert.Id);

            // Ending a window early is a delete, and it is audited like creating one.
            using (var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/silences/{createdSilenceId}"))
            {
                delete.Headers.Add("X-Ops-CSRF", csrf);
                Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(delete, Ct)).StatusCode);
            }

            silenceId = null;

            var audit = await client.GetFromJsonAsync<AuditList>("/api/audit?limit=100", Ct);
            Assert.Contains(audit!.Items, item => item.Action == "silence.create" && item.Target == createdSilenceId);
            Assert.Contains(audit.Items, item => item.Action == "silence.delete" && item.Target == createdSilenceId);
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
                if (silenceId is not null && csrf is not null)
                {
                    using var deleteSilence = new HttpRequestMessage(HttpMethod.Delete, $"/api/silences/{silenceId}");
                    deleteSilence.Headers.Add("X-Ops-CSRF", csrf);
                    await client.SendAsync(deleteSilence, Ct);
                }

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

    private static async Task<string> CreateRuleAsync(HttpClient client, string csrf)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/settings/rules")
        {
            Content = JsonContent.Create(new
            {
                name = "测试：静默",
                kind = "scrape",
                comparator = "gt",
                threshold = 0.5,
                forSeconds = 0,
                severity = "P3",
                enabled = true,
            }),
        };
        request.Headers.Add("X-OPS-CSRF", csrf);

        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RuleDto>(Ct))!.Id;
    }

    private static async Task<string> CreateFailingNodeAsync(HttpClient client, string csrf)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/nodes")
        {
            Content = JsonContent.Create(new
            {
                name = "silence-" + Guid.NewGuid().ToString("N")[..8],
                role = "relay",
                baseUrl = "http://127.0.0.1:1",
            }),
        };
        request.Headers.Add("X-OPS-CSRF", csrf);

        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<NodeDto>(Ct))!.Id;
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

        return (client, cookies);
    }

    public sealed record SilenceDto(string Id);

    public sealed record RuleDto(string Id);

    public sealed record NodeDto(string Id);

    public sealed record AuditList(List<AuditEntryDto> Items);

    public sealed record AuditEntryDto(string Action, string Target);

    public sealed record LoginResponse(string? Status, string? Challenge);
}
