using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemoteDesktop.Server.Ops;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// The rollup worker is opt-in. The code stays in the product, but a console that has no trend reader must not spend
/// a write pass every ten minutes filling a table nobody reads.
/// </summary>
public sealed class RollupRegistrationTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ops-rollup-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Best effort: a pooled handle on another platform must not fail the assertion that already ran.
            }
        }
    }

    [Fact]
    public void The_rollup_worker_is_not_registered_by_default()
    {
        var services = Build(withRollups: false);

        Assert.DoesNotContain(services, IsRollupWorker);
    }

    [Fact]
    public void The_rollup_worker_is_registered_when_enabled()
    {
        var services = Build(withRollups: true);

        Assert.Contains(services, IsRollupWorker);
    }

    private static bool IsRollupWorker(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(IHostedService)
        && descriptor.ImplementationType == typeof(RollupMaintenanceService);

    private List<ServiceDescriptor> Build(bool withRollups)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = Path.Combine(RepositoryRoot(), "src", "Server", "RemoteDesktop.Server.Ops"),
            EnvironmentName = "Development",
        });

        var settings = new Dictionary<string, string?>
        {
            ["Ops:Database:Provider"] = "sqlite",
            ["Ops:Database:Path"] = _databasePath,
        };
        if (withRollups)
        {
            settings["Ops:RollupEnabled"] = "true";
        }

        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddOps();

        return [.. builder.Services];
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RemoteDesktop.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the repository root from the test output directory.");
    }
}
