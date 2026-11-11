using Npgsql;

namespace RemoteDesktop.Server.Storage.IntegrationTests;

/// <summary>
/// A throw-away schema on a real PostgreSQL server, so that each test starts from an empty database and no test can
/// see another's rows. Only available when <c>RD_TEST_POSTGRES</c> names a server; otherwise the calling test is
/// skipped.
/// </summary>
internal sealed class PostgresTestSchema : IAsyncDisposable
{
    public const string EnvironmentVariable = "RD_TEST_POSTGRES";

    private readonly string _adminConnectionString;
    private readonly string _name;

    private PostgresTestSchema(string adminConnectionString, string name)
    {
        _adminConnectionString = adminConnectionString;
        _name = name;

        // Pooling is off so that dropping the schema never races a pooled connection of this test, and the pool of one
        // test cannot grow into the next one's.
        ConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            SearchPath = name,
            Pooling = false,
        }.ConnectionString;
    }

    /// <summary>A connection string whose search path is only this schema.</summary>
    public string ConnectionString { get; }

    /// <summary>Skips the calling test when the environment variable is not set, otherwise creates the schema.</summary>
    public static PostgresTestSchema Create()
    {
        var connectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(connectionString),
            $"PostgreSQL tests are opt-in. Start a server, for example "
            + "'docker run -d --name rd-pg-test -e POSTGRES_PASSWORD=test -p 127.0.0.1:55432:5432 postgres:18', "
            + $"and set {EnvironmentVariable} to its Npgsql connection string "
            + "(Host=127.0.0.1;Port=55432;Username=postgres;Password=test;Database=postgres).");

        var name = "rd_test_" + Guid.NewGuid().ToString("N");
        var admin = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
        using var connection = new NpgsqlConnection(admin);
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = $"CREATE SCHEMA \"{name}\";";
        create.ExecuteNonQuery();

        return new PostgresTestSchema(admin, name);
    }

    /// <summary>An open connection whose search path is this schema, for arranging and checking rows directly.</summary>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>Runs one statement in this schema.</summary>
    public async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var drop = connection.CreateCommand();
        drop.CommandText = $"DROP SCHEMA IF EXISTS \"{_name}\" CASCADE;";
        await drop.ExecuteNonQueryAsync();
    }
}
