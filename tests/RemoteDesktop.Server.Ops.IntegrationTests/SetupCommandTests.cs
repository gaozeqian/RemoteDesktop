using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// The installer's node command. Without it a fresh console has no rendezvous node, and its device page — which
/// proxies every question through one — has nothing to ask.
/// </summary>
public class SetupCommandTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ops-setup-{Guid.NewGuid():N}");

    public SetupCommandTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Seeding_registers_the_two_local_nodes_once()
    {
        await WriteSettingsAsync();

        var output = new StringWriter();
        var handled = SetupCommands.TryRun(
            [
                "--seed-local-nodes",
                "--config-directory", _directory,
                "--rendezvous-base-url", "https://127.0.1.1:5080",
                "--relay-base-url", "http://127.0.0.1:5083",
            ],
            output,
            out var exitCode);

        Assert.True(handled);
        Assert.Equal(0, exitCode);

        using var store = OpsStore.Open(Settings());
        var nodes = await store.ListNodesAsync(Ct);
        Assert.Equal(2, nodes.Count);
        Assert.Contains(nodes, node => node is { Name: "rendezvous-local", Role: "rendezvous", BaseUrl: "https://127.0.1.1:5080", Enabled: true });
        Assert.Contains(nodes, node => node is { Name: "relay-local", Role: "relay", BaseUrl: "http://127.0.0.1:5083", Enabled: true });

        // A reinstall runs the command again. The rows are already there, and the operator's list (including a row
        // they deleted) must not be rewritten.
        var second = new StringWriter();
        Assert.True(SetupCommands.TryRun(
            [
                "--seed-local-nodes",
                "--config-directory", _directory,
                "--rendezvous-base-url", "https://127.0.1.1:5080",
                "--relay-base-url", "http://127.0.0.1:5083",
            ],
            second,
            out var secondExitCode));

        Assert.Equal(0, secondExitCode);
        Assert.Equal(2, (await store.ListNodesAsync(Ct)).Count);
    }

    /// <summary>
    /// A TLS upgrade changes the scheme of the local nodes' addresses. The rows exist, so seeding does nothing; the
    /// sync command has to update the two local rows without touching an operator's own node or re-creating a local
    /// row that was deleted.
    /// </summary>
    [Fact]
    public async Task Syncing_updates_only_the_local_nodes_that_exist()
    {
        await WriteSettingsAsync();
        Assert.True(SetupCommands.TryRun(
            [
                "--seed-local-nodes",
                "--config-directory", _directory,
                "--rendezvous-base-url", "http://127.0.0.1:5080",
                "--relay-base-url", "http://127.0.0.1:5083",
            ],
            new StringWriter(),
            out _));

        using var store = OpsStore.Open(Settings());
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await store.CreateNodeAsync(
            new OpsNode("custom-id", "custom", "rendezvous", "http://custom.example:5080", null, true, null, null, now),
            Ct);
        var seeded = await store.ListNodesAsync(Ct);
        await store.DeleteNodeAsync(seeded.Single(node => node.Name == "relay-local").Id, Ct);

        var output = new StringWriter();
        Assert.True(SetupCommands.TryRun(
            [
                "--sync-local-nodes",
                "--config-directory", _directory,
                "--rendezvous-base-url", "https://127.0.1.1:5443",
                "--relay-base-url", "https://127.0.0.1:5083",
            ],
            output,
            out var exitCode));

        Assert.Equal(0, exitCode);
        var nodes = await store.ListNodesAsync(Ct);
        Assert.Contains(nodes, node => node is { Name: "rendezvous-local", BaseUrl: "https://127.0.1.1:5443" });
        Assert.DoesNotContain(nodes, node => node.Name == "relay-local"); // deleted by the operator, not re-created
        Assert.Contains(nodes, node => node is { Name: "custom", BaseUrl: "http://custom.example:5080" });
    }

    [Fact]
    public async Task A_node_address_that_is_not_http_is_refused_before_the_database_is_opened()
    {
        await WriteSettingsAsync();

        var output = new StringWriter();
        Assert.True(SetupCommands.TryRun(
            [
                "--seed-local-nodes",
                "--config-directory", _directory,
                "--rendezvous-base-url", "127.0.0.1:5080",
            ],
            output,
            out var exitCode));

        Assert.Equal(2, exitCode);
        Assert.Contains("http://", output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_directory, "ops.db")));
    }

    /// <summary>
    /// The packaged Windows file says %ProgramData%, and the service expands it. The command has to expand it the
    /// same way; treating it as a relative path creates the first administrator in
    /// "...\Ops\%ProgramData%\..." while the service uses the real database.
    /// </summary>
    [Fact]
    public void A_missing_configuration_directory_is_reported_without_a_stack_trace()
    {
        var missing = Path.Combine(_directory, "does-not-exist");
        var output = new StringWriter();

        Assert.True(SetupCommands.TryRun(
            [
                "--seed-local-nodes",
                "--config-directory", missing,
                "--rendezvous-base-url", "http://127.0.0.1:5080",
            ],
            output,
            out var exitCode));

        Assert.Equal(2, exitCode);
        Assert.Contains("不存在", output.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task A_relative_path_and_a_provider_with_spaces_resolve_like_the_service()
    {
        await WriteSettingsAsync("data/ops.db", provider: " SQLite ");
        var database = Path.Combine(_directory, "data", "ops.db");

        var output = new StringWriter();
        Assert.True(SetupCommands.TryRun(
            [
                "--seed-local-nodes",
                "--config-directory", _directory,
                "--rendezvous-base-url", "http://127.0.0.1:5080",
                "--relay-base-url", "http://127.0.0.1:5083",
            ],
            output,
            out var exitCode));

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(database), output.ToString());

        using var store = OpsStore.Open(new OpsDatabaseSettings { Provider = OpsDatabaseSettings.Sqlite, Path = database });
        Assert.Equal(2, (await store.ListNodesAsync(Ct)).Count);
    }

    [Fact]
    public async Task A_percent_variable_in_the_database_path_is_expanded_like_the_service_does()
    {
        var expanded = Path.Combine(_directory, "expanded");
        Environment.SetEnvironmentVariable("RD_TEST_OPS_DATA", expanded);
        try
        {
            await WriteSettingsAsync("%RD_TEST_OPS_DATA%/ops.db");

            var output = new StringWriter();
            Assert.True(SetupCommands.TryRun(
                [
                    "--seed-local-nodes",
                    "--config-directory", _directory,
                    "--rendezvous-base-url", "http://127.0.0.1:5080",
                    "--relay-base-url", "http://127.0.0.1:5083",
                ],
                output,
                out var exitCode));

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(expanded, "ops.db")), output.ToString());
            Assert.False(Directory.Exists(Path.Combine(_directory, "%RD_TEST_OPS_DATA%")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("RD_TEST_OPS_DATA", null);
        }
    }

    /// <summary>
    /// A connection string the driver cannot parse (an unbalanced quote in a password, say) used to escape as an
    /// unhandled exception: the installer printed a stack trace and the process aborted. The command has to say what
    /// is wrong and return a code instead.
    /// </summary>
    [Theory]
    [InlineData("--seed-local-nodes")]
    [InlineData("--sync-local-nodes")]
    [InlineData("--create-admin")]
    public async Task A_database_that_cannot_be_opened_is_reported_instead_of_crashing(string command)
    {
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "appsettings.json"),
            """
            {
              "Ops": {
                "Database": { "Provider": "postgres", "ConnectionString": "Host=db;Password=\"unterminated" }
              }
            }
            """,
            Ct);

        var arguments = command == "--create-admin"
            ? new[] { command, "admin", "--config-directory", _directory }
            : [command, "--config-directory", _directory, "--rendezvous-base-url", "http://127.0.0.1:5080"];

        var output = new StringWriter();
        Assert.True(SetupCommands.TryRun(arguments, output, out var exitCode));

        Assert.Equal(1, exitCode);
        Assert.Contains("数据库", output.ToString(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A file still held open is the test host's problem, not the result's.
        }
    }

    private Task WriteSettingsAsync(string databasePath = "ops.db", string provider = "sqlite") =>
        File.WriteAllTextAsync(
            Path.Combine(_directory, "appsettings.json"),
            $$"""
            {
              "Ops": {
                "Database": { "Provider": "{{provider}}", "Path": "{{databasePath}}" }
              }
            }
            """,
            Ct);

    private OpsDatabaseSettings Settings() =>
        new() { Provider = OpsDatabaseSettings.Sqlite, Path = Path.Combine(_directory, "ops.db") };
}
