using RemoteDesktop.Server.Storage;

namespace RemoteDesktop.Server.Storage.UnitTests;

/// <summary>
/// Which provider a settings file names, and what happens to the two keys earlier versions used. The legacy cases are
/// the important ones: getting them wrong is an upgrade that silently hands every device a new code.
/// </summary>
public class DatabaseSettingsTests
{
    /// <summary>A connection string on its own still means PostgreSQL, because that is what it used to mean.</summary>
    [Fact]
    public void A_connection_string_alone_still_means_postgres()
    {
        var settings = DatabaseSettings.Resolve(new DatabaseSettings(), "Host=db;Database=remotedesktop");

        Assert.Equal(DatabaseSettings.Postgres, settings.Provider);
        Assert.Equal("Host=db;Database=remotedesktop", settings.ConnectionString);
    }

    [Fact]
    public void Nothing_configured_means_memory()
    {
        var settings = DatabaseSettings.Resolve(new DatabaseSettings(), legacyConnectionString: null);

        Assert.Equal(DatabaseSettings.None, settings.Provider);
        settings.Validate();
    }

    [Fact]
    public void An_explicit_provider_wins_over_the_legacy_key()
    {
        var settings = DatabaseSettings.Resolve(
            new DatabaseSettings { Provider = "SQLite", Path = " /var/lib/remotedesktop/rendezvous.db " },
            "Host=db;Database=remotedesktop");

        Assert.Equal(DatabaseSettings.Sqlite, settings.Provider);
        Assert.Equal("/var/lib/remotedesktop/rendezvous.db", settings.Path);
        Assert.Null(settings.ConnectionString);
    }

    [Fact]
    public void Sqlite_without_a_path_is_refused()
    {
        var settings = DatabaseSettings.Resolve(new DatabaseSettings { Provider = DatabaseSettings.Sqlite }, null);

        var refused = Assert.Throws<InvalidOperationException>(settings.Validate);
        Assert.Contains("Rendezvous:Database:Path", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Postgres_without_a_connection_string_is_refused()
    {
        var settings = DatabaseSettings.Resolve(new DatabaseSettings { Provider = DatabaseSettings.Postgres }, null);

        var refused = Assert.Throws<InvalidOperationException>(settings.Validate);
        Assert.Contains("Rendezvous:Database:ConnectionString", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_provider_is_refused_and_the_message_names_the_ones_that_exist()
    {
        var refused = Assert.Throws<InvalidOperationException>(() =>
            DatabaseSettings.Resolve(new DatabaseSettings { Provider = "mysql" }, null));

        Assert.Contains(DatabaseSettings.Postgres, refused.Message, StringComparison.Ordinal);
        Assert.Contains(DatabaseSettings.Sqlite, refused.Message, StringComparison.Ordinal);
    }
}
