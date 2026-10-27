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

    [Fact]
    public void A_relative_path_is_resolved_against_the_given_directory()
    {
        var baseDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ops-base"));
        var resolved = OpsDatabaseSettings.ResolvePath("sqlite", "data/ops.db", baseDirectory);

        Assert.Equal(Path.Combine(baseDirectory, "data", "ops.db"), Path.GetFullPath(resolved!));
    }

    [Fact]
    public void An_absolute_path_is_left_alone()
    {
        var absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ops.db"));

        Assert.Equal(absolute, OpsDatabaseSettings.ResolvePath("sqlite", absolute, Path.GetTempPath()));
    }

    [Fact]
    public void The_provider_is_trimmed_before_the_path_is_resolved()
    {
        var baseDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ops-base"));
        var resolved = OpsDatabaseSettings.ResolvePath(" sqlite ", "ops.db", baseDirectory);

        Assert.Equal(Path.Combine(baseDirectory, "ops.db"), resolved);
    }

    [Fact]
    public void An_expansion_that_produces_nothing_is_treated_as_missing()
    {
        if (!OperatingSystem.IsWindows())
        {
            // %VAR% expansion is a Windows settings-file concern; the guard keeps the test honest where it cannot run.
            return;
        }

        Environment.SetEnvironmentVariable("RD_TEST_EMPTY_OPS_PATH", string.Empty);
        try
        {
            Assert.Null(OpsDatabaseSettings.ResolvePath("sqlite", "%RD_TEST_EMPTY_OPS_PATH%", "/srv/remotedesktop"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("RD_TEST_EMPTY_OPS_PATH", null);
        }
    }
}
