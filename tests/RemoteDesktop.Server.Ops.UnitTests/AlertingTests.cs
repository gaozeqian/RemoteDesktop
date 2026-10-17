using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.UnitTests;

/// <summary>
/// The two halves of an alert rule: the comparison, and the "for how long" timer. A rule that fires on one bad sample
/// is worse than no rule — it teaches people to ignore the page.
/// </summary>
public class AlertingTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(11, "gt", 10, true)]
    [InlineData(10, "gt", 10, false)]
    [InlineData(9, "lt", 10, true)]
    [InlineData(10, "lt", 10, false)]
    public void Comparators_do_what_they_say(double value, string comparator, double threshold, bool expected)
    {
        Assert.Equal(expected, AlertComparators.Evaluate(value, comparator, threshold));
    }

    [Fact]
    public void An_unknown_comparator_never_fires()
    {
        Assert.False(AlertComparators.Evaluate(100, "contains", 1));
    }

    [Fact]
    public void A_condition_that_has_not_held_long_enough_does_not_alert()
    {
        var time = new ManualTimeProvider(Start);
        var machine = new AlertStateMachine(time);

        Assert.Equal(AlertDecision.None, machine.Observe("key", firing: true, forSeconds: 30, hasOpenAlert: false));

        time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(AlertDecision.None, machine.Observe("key", firing: true, forSeconds: 30, hasOpenAlert: false));

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(AlertDecision.Open, machine.Observe("key", firing: true, forSeconds: 30, hasOpenAlert: false));
    }

    [Fact]
    public void A_condition_that_clears_before_the_window_resets_the_timer()
    {
        var time = new ManualTimeProvider(Start);
        var machine = new AlertStateMachine(time);

        machine.Observe("key", firing: true, forSeconds: 30, hasOpenAlert: false);
        time.Advance(TimeSpan.FromSeconds(20));
        machine.Observe("key", firing: false, forSeconds: 30, hasOpenAlert: false);
        time.Advance(TimeSpan.FromSeconds(20));
        machine.Observe("key", firing: true, forSeconds: 30, hasOpenAlert: false);

        // The second stretch is only 0 seconds old; the first one was cleared.
        Assert.Equal(AlertDecision.None, machine.Observe("key", firing: true, forSeconds: 30, hasOpenAlert: false));
    }

    [Fact]
    public void An_open_alert_is_not_opened_twice()
    {
        var time = new ManualTimeProvider(Start);
        var machine = new AlertStateMachine(time);

        Assert.Equal(AlertDecision.Open, machine.Observe("key", firing: true, forSeconds: 0, hasOpenAlert: false));
        Assert.Equal(AlertDecision.None, machine.Observe("key", firing: true, forSeconds: 0, hasOpenAlert: true));
    }

    [Fact]
    public void A_cleared_condition_resolves_an_open_alert()
    {
        var time = new ManualTimeProvider(Start);
        var machine = new AlertStateMachine(time);

        Assert.Equal(AlertDecision.Resolve, machine.Observe("key", firing: false, forSeconds: 0, hasOpenAlert: true));
        Assert.Equal(AlertDecision.None, machine.Observe("key", firing: false, forSeconds: 0, hasOpenAlert: false));
    }

    [Fact]
    public void An_audit_chain_verifies_when_it_is_untouched_and_names_the_first_edited_entry()
    {
        var first = Record(AuditChain.Genesis, "a", 1, "auth.login");
        var second = Record(first.Hash, "b", 2, "alert.ack");
        var third = Record(second.Hash, "c", 3, "alert.open");

        Assert.True(AuditVerifier.Verify([first, second, third]).IsValid);

        var tampered = second with { Action = "alert.silence" };
        var verification = AuditVerifier.Verify([first, tampered, third]);

        Assert.False(verification.IsValid);
        Assert.Equal("b", verification.FirstInvalidId);
    }

    [Fact]
    public void A_removed_entry_is_detected_by_the_missing_link()
    {
        var first = Record(AuditChain.Genesis, "a", 1, "auth.login");
        var second = Record(first.Hash, "b", 2, "alert.ack");
        var third = Record(second.Hash, "c", 3, "alert.open");

        var verification = AuditVerifier.Verify([first, third]);

        Assert.False(verification.IsValid);
        Assert.Equal("c", verification.FirstInvalidId);
    }

    private static AuditRecord Record(string previous, string id, long timestamp, string action)
    {
        var hash = AuditChain.Compute(previous, AuditChain.Fields(timestamp, "ops", action, "-", "{}", "10.0.0.1", "success"));
        return new AuditRecord(id, timestamp, "ops", action, "-", "{}", "10.0.0.1", "success", previous, hash);
    }
}
