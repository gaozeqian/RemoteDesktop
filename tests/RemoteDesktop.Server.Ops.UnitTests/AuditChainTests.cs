using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.UnitTests;

/// <summary>
/// The chain has one job: make a single edited row detectable, and make rearranging fields detectable too. Both are
/// tests rather than comments because both fail silently.
/// </summary>
public class AuditChainTests
{
    [Fact]
    public void The_same_entry_hashes_the_same_way_twice()
    {
        var fields = AuditChain.Fields(1_700_000_000_000, "ops", "auth.login", "u1", "{}", "10.0.0.1", "success");

        Assert.Equal(
            AuditChain.Compute(AuditChain.Genesis, fields),
            AuditChain.Compute(AuditChain.Genesis, fields));
    }

    [Fact]
    public void Changing_one_field_changes_the_hash()
    {
        var original = AuditChain.Fields(1, "ops", "auth.login", "u1", "{}", "10.0.0.1", "success");
        var edited = AuditChain.Fields(1, "ops", "auth.login", "u1", "{}", "10.0.0.1", "denied");

        Assert.NotEqual(
            AuditChain.Compute(AuditChain.Genesis, original),
            AuditChain.Compute(AuditChain.Genesis, edited));
    }

    [Fact]
    public void Field_boundaries_are_part_of_the_hash()
    {
        // Without length prefixes these two would hash identically, and a log whose fields can be shifted is not a log.
        var splitOneWay = AuditChain.Compute(AuditChain.Genesis, ["ab", "c"]);
        var splitAnother = AuditChain.Compute(AuditChain.Genesis, ["a", "bc"]);

        Assert.NotEqual(splitOneWay, splitAnother);
    }

    [Fact]
    public void The_same_fields_after_a_different_history_hash_differently()
    {
        // Two entries with identical content but different predecessors must not produce the same digest: that is what
        // makes deleting or reordering a row visible.
        var fields = AuditChain.Fields(1, "ops", "auth.login", "u1", "{}", "10.0.0.1", "success");
        var afterGenesis = AuditChain.Compute(AuditChain.Genesis, fields);
        var afterAnotherEntry = AuditChain.Compute(new string('a', 64), fields);

        Assert.NotEqual(afterGenesis, afterAnotherEntry);
    }
}
