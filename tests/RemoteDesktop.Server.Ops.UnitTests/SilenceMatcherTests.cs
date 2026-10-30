using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.UnitTests;

/// <summary>
/// A silence suppresses the notifications for one rule, one node, or both, until a moment in time. It never touches
/// the alert itself — that is the whole point of the feature, and the reason these rules are a pure function.
/// </summary>
public sealed class SilenceMatcherTests
{
    private const long Now = 1_000_000;

    [Fact]
    public void A_rule_silence_covers_that_rule_on_every_node_and_nothing_else()
    {
        SilenceWindow[] silences = [new("rule-a", Node: null, UntilAt: Now + 60_000)];

        Assert.True(SilenceMatcher.IsSilenced(silences, "rule-a", node: "web-1", Now));
        Assert.True(SilenceMatcher.IsSilenced(silences, "rule-a", node: null, Now));
        Assert.False(SilenceMatcher.IsSilenced(silences, "rule-b", node: "web-1", Now));
    }

    [Fact]
    public void A_node_silence_covers_that_node_and_nothing_else()
    {
        SilenceWindow[] silences = [new(RuleId: null, "web-1", UntilAt: Now + 60_000)];

        Assert.True(SilenceMatcher.IsSilenced(silences, "rule-a", "web-1", Now));
        Assert.False(SilenceMatcher.IsSilenced(silences, "rule-a", "web-2", Now));

        // A cluster-wide rule has no node, so a node silence cannot cover it.
        Assert.False(SilenceMatcher.IsSilenced(silences, "rule-a", node: null, Now));
    }

    [Fact]
    public void A_window_stops_covering_the_moment_it_closes()
    {
        SilenceWindow[] silences = [new("rule-a", Node: null, UntilAt: Now)];

        Assert.False(SilenceMatcher.IsSilenced(silences, "rule-a", node: null, Now));
        Assert.False(SilenceMatcher.IsSilenced(silences, "rule-a", node: null, Now + 1));
    }

    [Fact]
    public void No_silences_at_all_never_suppresses()
    {
        Assert.False(SilenceMatcher.IsSilenced([], "rule-a", node: "web-1", Now));
    }

    /// <summary>The node is the only part of an alert that is not already a column, so its key is parsed in one place.</summary>
    [Fact]
    public void The_node_is_read_back_out_of_the_dedupe_key()
    {
        Assert.Equal("web-1", AlertDedupeKeys.NodeOf(AlertDedupeKeys.ForNode("rule-a", "web-1")));
        Assert.Null(AlertDedupeKeys.NodeOf(AlertDedupeKeys.ForRule("rule-a")));

        // A node name that itself contains the marker still yields the name that was put in.
        var odd = AlertDedupeKeys.ForNode("rule-a", "web:node:1");
        Assert.Equal("web:node:1", AlertDedupeKeys.NodeOf(odd));
    }
}
