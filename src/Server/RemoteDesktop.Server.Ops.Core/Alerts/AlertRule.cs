namespace RemoteDesktop.Server.Ops.Core;

/// <summary>
/// One alert rule, as stored. Structured columns rather than an expression string: a parser is a language to maintain,
/// and the two question shapes here — one metric over a threshold, or one metric over another — cover what an ops
/// console actually asks.
/// </summary>
/// <param name="Kind">One of <see cref="AlertRuleKinds"/>.</param>
/// <param name="Aggregation">For gauges: <c>avg</c>, <c>max</c> or <c>min</c>. Ignored for counters, which are rated.</param>
/// <param name="ForSeconds">How long the condition must hold before it becomes an alert. Zero means immediately.</param>
public sealed record AlertRule(
    string Id,
    string Name,
    string Kind,
    string? Metric,
    string? Numerator,
    string? Denominator,
    string Aggregation,
    string Comparator,
    double Threshold,
    int WindowSeconds,
    int ForSeconds,
    string Severity,
    bool Enabled);

/// <summary>The rule kinds the evaluator understands.</summary>
public static class AlertRuleKinds
{
    /// <summary>One metric against a threshold.</summary>
    public const string SingleMetric = "single";

    /// <summary>One metric divided by another, against a threshold.</summary>
    public const string Ratio = "ratio";

    /// <summary>A node whose last scrape failed — the condition that is not a metric at all.</summary>
    public const string Scrape = "scrape";

    /// <summary>Mean RTT reported by controllers over the window. Needs client telemetry, which is off by default.</summary>
    public const string SessionRtt = "session.rtt";

    /// <summary>Share of reported sessions that went direct (P2P) rather than through a relay.</summary>
    public const string SessionDirectRatio = "session.direct";

    /// <summary>Whether <paramref name="kind"/> is one this evaluator computes from a session's own quality.</summary>
    public static bool IsSessionQuality(string? kind) =>
        string.Equals(kind, SessionRtt, StringComparison.Ordinal)
        || string.Equals(kind, SessionDirectRatio, StringComparison.Ordinal);
}

/// <summary>Comparators, spelled as the console stores them.</summary>
public static class AlertComparators
{
    public const string GreaterThan = "gt";
    public const string LessThan = "lt";

    /// <summary>True when <paramref name="comparator"/> is one of the two supported names.</summary>
    public static bool IsValid(string? comparator) =>
        string.Equals(comparator, GreaterThan, StringComparison.OrdinalIgnoreCase)
        || string.Equals(comparator, LessThan, StringComparison.OrdinalIgnoreCase);

    /// <summary>Applies the comparison. An unknown comparator never fires, which is the safe direction.</summary>
    public static bool Evaluate(double value, string comparator, double threshold)
    {
        if (string.Equals(comparator, GreaterThan, StringComparison.OrdinalIgnoreCase))
        {
            return value > threshold;
        }

        return string.Equals(comparator, LessThan, StringComparison.OrdinalIgnoreCase) && value < threshold;
    }
}

/// <summary>What one observation means for the alert it belongs to.</summary>
public enum AlertDecision
{
    /// <summary>Nothing changed.</summary>
    None,

    /// <summary>The condition has held long enough; there is no open alert yet.</summary>
    Open,

    /// <summary>The condition cleared while an alert was open.</summary>
    Resolve,
}

/// <summary>
/// Holds the "for how long" half of a rule: a condition that flaps once must not page anybody, and one that has
/// already been alerted must not alert again.
/// </summary>
/// <remarks>
/// Pending timers live in memory. A restart forgets how long a condition has been true, so an alert fires again after
/// the restart — the alternative, persisting a timer per rule, is a table that only ever holds a handful of rows and
/// is never read by a human. Duplicate alerts after a restart are acceptable; missed ones are not.
/// </remarks>
public sealed class AlertStateMachine(TimeProvider time)
{
    private readonly Dictionary<string, DateTimeOffset> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// Folds one observation in. <paramref name="hasOpenAlert"/> is the database's answer, not the machine's, so a
    /// restart cannot produce a second alert for a condition that is already open.
    /// </summary>
    public AlertDecision Observe(string dedupeKey, bool firing, int forSeconds, bool hasOpenAlert)
    {
        ArgumentException.ThrowIfNullOrEmpty(dedupeKey);

        if (!firing)
        {
            _pending.Remove(dedupeKey);
            return hasOpenAlert ? AlertDecision.Resolve : AlertDecision.None;
        }

        if (hasOpenAlert)
        {
            _pending.Remove(dedupeKey);
            return AlertDecision.None;
        }

        var now = time.GetUtcNow();
        if (!_pending.TryGetValue(dedupeKey, out var since))
        {
            if (forSeconds <= 0)
            {
                return AlertDecision.Open;
            }

            _pending[dedupeKey] = now;
            return AlertDecision.None;
        }

        if ((now - since).TotalSeconds < forSeconds)
        {
            return AlertDecision.None;
        }

        _pending.Remove(dedupeKey);
        return AlertDecision.Open;
    }
}
