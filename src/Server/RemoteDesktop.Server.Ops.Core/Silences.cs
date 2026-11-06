namespace RemoteDesktop.Server.Ops.Core;

/// <summary>
/// The identity an alert is deduplicated and silenced by. One place builds the string and one place takes it apart:
/// the node name is the only part of an alert that is not already in a column of its own.
/// </summary>
public static class AlertDedupeKeys
{
    /// <summary>A rule with no node: a metric, ratio or session-quality rule sees the whole deployment.</summary>
    public static string ForRule(string ruleId) => $"rule:{ruleId}";

    /// <summary>One rule on one node: what a scrape rule produces, once per node.</summary>
    public static string ForNode(string ruleId, string node) => $"rule:{ruleId}:node:{node}";

    /// <summary>The node part of a key, or <c>null</c> for a key that has none.</summary>
    public static string? NodeOf(string dedupeKey)
    {
        ArgumentNullException.ThrowIfNull(dedupeKey);

        var marker = dedupeKey.IndexOf(":node:", StringComparison.Ordinal);
        return marker < 0 ? null : dedupeKey[(marker + ":node:".Length)..];
    }
}

/// <summary>One silence, reduced to what matching needs.</summary>
/// <param name="RuleId">The rule it covers, or <c>null</c> for any rule.</param>
/// <param name="Node">The node it covers, or <c>null</c> for any node.</param>
/// <param name="UntilAt">Unix milliseconds; the window is closed from this instant on.</param>
public readonly record struct SilenceWindow(string? RuleId, string? Node, long UntilAt)
{
    /// <summary>Whether this silence covers that alert at that moment.</summary>
    public bool Covers(string ruleId, string? node, long now) =>
        UntilAt > now
        && (RuleId is null || string.Equals(RuleId, ruleId, StringComparison.Ordinal))
        && (Node is null || string.Equals(Node, node, StringComparison.Ordinal));
}

/// <summary>Applies the silences a pass loaded.</summary>
/// <remarks>
/// A silenced transition is not queued: there is no attempt row, so the mail that the silence suppressed is not sent
/// after it ends either. That is the difference between "this is noisy for a night" and "tell me in the morning".
/// The alert itself opens, resolves and is audited exactly as before — the silence is about the pages, not the history.
/// </remarks>
public static class SilenceMatcher
{
    public static bool IsSilenced(IReadOnlyList<SilenceWindow> silences, string ruleId, string? node, long now)
    {
        ArgumentNullException.ThrowIfNull(silences);

        for (var index = 0; index < silences.Count; index++)
        {
            if (silences[index].Covers(ruleId, node, now))
            {
                return true;
            }
        }

        return false;
    }
}
