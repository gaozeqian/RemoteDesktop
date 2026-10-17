using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.UnitTests;

/// <summary>
/// A console with nowhere to write has nothing to serve, so the settings refuse to fall back to memory. The messages
/// name the key because the person reading them is looking at a config file at the time.
/// </summary>
public class OpsDatabaseSettingsTests
{
    [Fact]
    public void Sqlite_needs_a_path()
    {
        var settings = new OpsDatabaseSettings { Provider = "sqlite" };

        var error = Assert.Throws<InvalidOperationException>(settings.Validate);
        Assert.Contains("Ops:Database:Path", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Postgres_needs_a_connection_string()
    {
        var settings = new OpsDatabaseSettings { Provider = "Postgres" };

        var error = Assert.Throws<InvalidOperationException>(settings.Validate);
        Assert.Contains("Ops:Database:ConnectionString", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("")]
    [InlineData("sqlserver")]
    public void Unknown_providers_are_refused(string provider)
    {
        var settings = new OpsDatabaseSettings { Provider = provider };

        Assert.Throws<InvalidOperationException>(settings.Validate);
    }

    [Fact]
    public void A_complete_sqlite_section_validates()
    {
        var settings = new OpsDatabaseSettings { Provider = "SQLite", Path = "/tmp/ops.db" };

        settings.Validate();
        Assert.Equal(OpsDatabaseSettings.Sqlite, OpsDatabaseSettings.Canonical(settings.Provider));
    }
}
