using System.Text.Json;
using RemoteDesktop.Server.Ops.Auth;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops;

/// <summary>What the rule form posts. Every field optional so a partial edit cannot silently blank a column.</summary>
public sealed record UpsertRuleRequest(
    string? Id,
    string? Name,
    string? Kind,
    string? Metric,
    string? Numerator,
    string? Denominator,
    string? Aggregation,
    string? Comparator,
    double? Threshold,
    int? WindowSeconds,
    int? ForSeconds,
    string? Severity,
    bool? Enabled);

/// <summary>Alerts, their acknowledgement, and the rules that produce them.</summary>
public static class AlertEndpoints
{
    /// <summary>Maps <c>/api/alerts</c> and <c>/api/settings/rules</c>.</summary>
    public static void MapOpsAlerts(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/alerts", async (string? state, int? limit, OpsStore store, CancellationToken cancellationToken) =>
        {
            var rows = await store.ListAlertsAsync(state, limit ?? 100, cancellationToken).ConfigureAwait(false);
            return Results.Ok(rows.Select(Describe));
        }).RequireAuthorization(OpsAuth.ViewerPolicy);

        endpoints.MapPost("/api/alerts/{id}/ack", async (
            string id,
            HttpContext context,
            OpsStore store,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var actor = context.User.Identity?.Name ?? "unknown";
            var at = time.GetUtcNow();

            if (!await store.AckAlertAsync(id, actor, at.ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            await AuditAsync(store, context, actor, "alert.ack", id, at, cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        }).RequireAuthorization(OpsAuth.OperatorPolicy);

        var rules = endpoints.MapGroup("/api/settings/rules").RequireAuthorization(OpsAuth.ViewerPolicy);

        rules.MapGet("/", async (OpsStore store, CancellationToken cancellationToken) =>
        {
            var rows = await store.ListAlertRulesAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(rows.Select(DescribeRule));
        });

        rules.MapPost("/", async (
            UpsertRuleRequest request,
            HttpContext context,
            OpsStore store,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            if (!TryBuild(request, out var rule, out var message))
            {
                return Results.BadRequest(new { error = "rule", message });
            }

            if (await UnknownMetricAsync(rule, store, cancellationToken).ConfigureAwait(false) is { } unknown)
            {
                return Results.BadRequest(new { error = "rule", message = unknown });
            }

            await store.UpsertAlertRuleAsync(rule, cancellationToken).ConfigureAwait(false);
            await AuditAsync(store, context, context.User.Identity?.Name ?? "unknown", "alert.rule.save", rule.Name, time.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(DescribeRule(rule));
        }).RequireAuthorization(OpsAuth.AdminPolicy);

        rules.MapDelete("/{id}", async (
            string id,
            HttpContext context,
            OpsStore store,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            if (!await store.DeleteAlertRuleAsync(id, cancellationToken).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            await AuditAsync(store, context, context.User.Identity?.Name ?? "unknown", "alert.rule.delete", id, time.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
            return Results.NoContent();
        }).RequireAuthorization(OpsAuth.AdminPolicy);
    }

    private static bool TryBuild(UpsertRuleRequest request, out AlertRule rule, out string? message)
    {
        rule = null!;
        message = null;

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            message = "name is required";
            return false;
        }

        var kind = request.Kind?.Trim() switch
        {
            var value when string.Equals(value, AlertRuleKinds.SingleMetric, StringComparison.OrdinalIgnoreCase) => AlertRuleKinds.SingleMetric,
            var value when string.Equals(value, AlertRuleKinds.Ratio, StringComparison.OrdinalIgnoreCase) => AlertRuleKinds.Ratio,
            var value when string.Equals(value, AlertRuleKinds.Scrape, StringComparison.OrdinalIgnoreCase) => AlertRuleKinds.Scrape,
            var value when string.Equals(value, AlertRuleKinds.SessionRtt, StringComparison.OrdinalIgnoreCase) => AlertRuleKinds.SessionRtt,
            var value when string.Equals(value, AlertRuleKinds.SessionDirectRatio, StringComparison.OrdinalIgnoreCase) => AlertRuleKinds.SessionDirectRatio,
            _ => null,
        };

        if (kind is null)
        {
            message = "kind must be single, ratio, scrape, session.rtt or session.direct";
            return false;
        }

        if (!AlertComparators.IsValid(request.Comparator ?? AlertComparators.GreaterThan))
        {
            message = "comparator must be gt or lt";
            return false;
        }

        var severity = request.Severity?.Trim().ToUpperInvariant() switch
        {
            "P1" => "P1",
            "P2" => "P2",
            "P3" => "P3",
            _ => "P2",
        };

        if (kind == AlertRuleKinds.SingleMetric && string.IsNullOrWhiteSpace(request.Metric))
        {
            message = "metric is required for a single-metric rule";
            return false;
        }

        if (kind == AlertRuleKinds.Ratio && (string.IsNullOrWhiteSpace(request.Numerator) || string.IsNullOrWhiteSpace(request.Denominator)))
        {
            message = "numerator and denominator are required for a ratio rule";
            return false;
        }

        rule = new AlertRule(
            string.IsNullOrEmpty(request.Id) ? Guid.NewGuid().ToString("N") : request.Id,
            name,
            kind,
            request.Metric?.Trim(),
            request.Numerator?.Trim(),
            request.Denominator?.Trim(),
            Aggregation(request.Aggregation),
            request.Comparator ?? AlertComparators.GreaterThan,
            request.Threshold ?? 0,
            Math.Clamp(request.WindowSeconds ?? 300, 30, 86_400),
            Math.Clamp(request.ForSeconds ?? 60, 0, 86_400),
            severity,
            request.Enabled ?? true);

        return true;
    }

    private static async Task<string?> UnknownMetricAsync(AlertRule rule, OpsStore store, CancellationToken cancellationToken)
    {
        if (string.Equals(rule.Kind, AlertRuleKinds.SingleMetric, StringComparison.Ordinal)
            && rule.Metric is { } metric
            && await store.ReadMetricKindAsync(metric, cancellationToken).ConfigureAwait(false) is null)
        {
            return $"metric '{metric}' has never been scraped; wait for the first scrape or correct the name";
        }

        if (string.Equals(rule.Kind, AlertRuleKinds.Ratio, StringComparison.Ordinal))
        {
            if (rule.Numerator is { } numerator
                && await store.ReadMetricKindAsync(numerator, cancellationToken).ConfigureAwait(false) is null)
            {
                return $"numerator '{numerator}' has never been scraped";
            }

            if (rule.Denominator is { } denominator
                && await store.ReadMetricKindAsync(denominator, cancellationToken).ConfigureAwait(false) is null)
            {
                return $"denominator '{denominator}' has never been scraped";
            }
        }

        return null;
    }

    /// <summary>The stored spelling of an aggregation, defaulting to the one that needs no explanation.</summary>
    private static string Aggregation(string? value) =>
        string.Equals(value?.Trim(), "max", StringComparison.OrdinalIgnoreCase) ? "max"
        : string.Equals(value?.Trim(), "min", StringComparison.OrdinalIgnoreCase) ? "min"
        : "avg";

    private static object Describe(OpsAlertRow row) => new
    {
        id = row.Id,
        ruleId = row.RuleId,
        severity = row.Severity,
        dedupeKey = row.DedupeKey,
        state = row.State,
        value = row.Value,
        openedAt = row.OpenedAt,
        ackedAt = row.AckedAt,
        ackedBy = row.AckedBy,
        resolvedAt = row.ResolvedAt,
        active = row.ResolvedAt is null,
    };

    private static object DescribeRule(AlertRule rule) => new
    {
        id = rule.Id,
        name = rule.Name,
        kind = rule.Kind,
        metric = rule.Metric,
        numerator = rule.Numerator,
        denominator = rule.Denominator,
        aggregation = rule.Aggregation,
        comparator = rule.Comparator,
        threshold = rule.Threshold,
        windowSeconds = rule.WindowSeconds,
        forSeconds = rule.ForSeconds,
        severity = rule.Severity,
        enabled = rule.Enabled,
    };

    private static Task<string> AuditAsync(
        OpsStore store,
        HttpContext context,
        string actor,
        string action,
        string target,
        DateTimeOffset at,
        CancellationToken cancellationToken) =>
        store.AppendAuditAsync(
            new OpsAuditEntry(
                Guid.NewGuid().ToString("N"),
                at.ToUnixTimeMilliseconds(),
                actor,
                action,
                target,
                JsonSerializer.Serialize(new { ip = context.Connection.RemoteIpAddress?.ToString() }, OpsEventJson.Options),
                context.Connection.RemoteIpAddress?.ToString() ?? "-",
                "success"),
            cancellationToken);
}
