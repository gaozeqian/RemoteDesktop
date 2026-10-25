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

    private Task WriteSettingsAsync() =>
        File.WriteAllTextAsync(
            Path.Combine(_directory, "appsettings.json"),
            """
            {
              "Ops": {
                "Database": { "Provider": "sqlite", "Path": "ops.db" }
              }
            }
            """,
            Ct);

    private OpsDatabaseSettings Settings() =>
        new() { Provider = OpsDatabaseSettings.Sqlite, Path = Path.Combine(_directory, "ops.db") };
}
