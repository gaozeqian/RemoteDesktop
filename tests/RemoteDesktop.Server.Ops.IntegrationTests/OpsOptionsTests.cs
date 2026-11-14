using RemoteDesktop.Server.Ops;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// The defaults that decide whether a new console writes downsampled metric buckets nobody reads.
/// </summary>
public sealed class OpsOptionsTests
{
    [Fact]
    public void Rollups_are_off_by_default_and_their_retention_does_not_delete_anything()
    {
        var options = new OpsOptions();
        options.Database.Path = Path.Combine(Path.GetTempPath(), "ops-options-test.db");

        Assert.False(options.RollupEnabled);
        Assert.Equal(0, options.Retention.RollupDays);

        // The defaults still have to be a configuration the host accepts, not just values that happen to be false.
        options.Validate();
    }

    [Fact]
    public void A_deployment_can_enable_rollups_and_a_longer_retention_together()
    {
        var options = new OpsOptions
        {
            RollupEnabled = true,
            Retention = new RetentionSettings { RollupDays = 365 },
        };
        options.Database.Path = Path.Combine(Path.GetTempPath(), "ops-options-test.db");

        options.Validate();

        Assert.True(options.RollupEnabled);
        Assert.Equal(365, options.Retention.RollupDays);
    }
}
