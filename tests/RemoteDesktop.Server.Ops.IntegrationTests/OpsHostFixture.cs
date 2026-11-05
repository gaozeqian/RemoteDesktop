using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Server.Ops;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// One console instance for the whole test class: a real Kestrel socket on a free loopback port and a throwaway SQLite
/// file. A test server would skip the middleware order and the cookie handling — the two things these tests exist for.
/// </summary>
public sealed class OpsHostFixture : IAsyncLifetime
{
    /// <summary>The password the seeded accounts share.</summary>
    public const string Password = "correct horse battery staple";

    private WebApplication? _app;
    private string _databasePath = string.Empty;

    /// <summary>Base address of the running console.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>The host's services, for driving the scraper by hand.</summary>
    public IServiceProvider Services => _app!.Services;

    /// <summary>The clock every service in the host reads.</summary>
    public TestTimeProvider Time { get; } = new();

    /// <summary>The host's current time, for events and signatures that must agree with it.</summary>
    public DateTimeOffset Now => Time.GetUtcNow();

    /// <summary>A TOTP code for the next time step, so a single-use code is never presented twice.</summary>
    public string NextTotp(string secret) =>
        Totp.Compute(secret, Time.Advance(TimeSpan.FromSeconds(Totp.StepSeconds)));

    /// <summary>TOTP secret of the <c>admin</c> account.</summary>
    public string AdminSecret { get; } = Totp.GenerateSecret();

    /// <summary>TOTP secret of the <c>lockme</c> account, kept separate so a lockout test cannot lock the admin out.</summary>
    public string LockedSecret { get; } = Totp.GenerateSecret();

    /// <summary>TOTP secret of the <c>operator</c> account — the two-person review needs a requester who is not an admin.</summary>
    public string OperatorSecret { get; } = Totp.GenerateSecret();

    /// <summary>
    /// TOTP secret of an account reserved for second-factor tests, so a lockout test cannot disturb the accounts the
    /// action-approval tests sign in with.
    /// </summary>
    public string TotpTestSecret { get; } = Totp.GenerateSecret();

    /// <summary>The HMAC key servers sign event batches with, as configured for the test host.</summary>
    public byte[] IngestKey { get; } = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    /// <summary>A client with its own cookie jar — sessions must not leak between tests.</summary>
    public (HttpClient Http, CookieContainer Cookies) CreateClient()
    {
        var cookies = new CookieContainer();
        var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true, AllowAutoRedirect = false };
        return (new HttpClient(handler) { BaseAddress = BaseAddress }, cookies);
    }

    /// <summary>Starts the host, then seeds two accounts.</summary>
    public async ValueTask InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"ops-tests-{Guid.NewGuid():N}.db");
        var contentRoot = Path.Combine(RepositoryRoot(), "src", "Server", "RemoteDesktop.Server.Ops");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = contentRoot,
            EnvironmentName = "Development",
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ops:Database:Provider"] = "sqlite",
            ["Ops:Database:Path"] = _databasePath,
            ["Ops:RequireTotp"] = "true",

            // The background scraper runs once at start-up, before any node exists, then waits. An hour keeps it from
            // adding samples while a test is counting them.
            ["Ops:ScrapeIntervalSeconds"] = "3600",
            ["Ops:AlertIntervalSeconds"] = "3600",

            ["Ops:Ingest:Keys:k1"] = Convert.ToBase64String(IngestKey),

            // Kestrel reads its addresses from configuration; port 0 asks the OS for a free one.
            ["urls"] = "http://127.0.0.1:0",
        });

        builder.Logging.ClearProviders();
        builder.AddOps();

        // Replace the system clock AddOps registered. Every server-side time read — session expiry, throttle windows,
        // event signatures, rule windows — must agree with the codes the tests compute.
        builder.Services.AddSingleton<TimeProvider>(Time);

        _app = builder.Build();
        _app.UseOps();
        await _app.StartAsync(TestContext.Current.CancellationToken);

        var server = _app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.First();
        BaseAddress = new Uri(address);

        var store = _app.Services.GetRequiredService<OpsStore>();
        var now = Time.GetUtcNow().ToUnixTimeMilliseconds();
        await store.CreateUserAsync(
            new OpsAdminUser(Guid.NewGuid().ToString("N"), "admin", PasswordHasher.Hash(Password), AdminSecret, OpsRoles.Admin, false, now));
        await store.CreateUserAsync(
            new OpsAdminUser(Guid.NewGuid().ToString("N"), "lockme", PasswordHasher.Hash(Password), LockedSecret, OpsRoles.Viewer, false, now));
        await store.CreateUserAsync(
            new OpsAdminUser(Guid.NewGuid().ToString("N"), "operator", PasswordHasher.Hash(Password), OperatorSecret, OpsRoles.Operator, false, now));
        await store.CreateUserAsync(
            new OpsAdminUser(Guid.NewGuid().ToString("N"), "totpuser", PasswordHasher.Hash(Password), TotpTestSecret, OpsRoles.Viewer, false, now));
    }

    /// <summary>Stops the host and removes the database and its WAL sidecars.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        // Windows refuses to delete a file that an open handle still points at, and Microsoft.Data.Sqlite pools its
        // connections by default. Disposing the host returns them to the pool rather than closing them, so the
        // pooled handles have to go before the database can be removed. Unix unlinks them either way.
        SqliteConnection.ClearAllPools();

        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RemoteDesktop.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root could not be found.");
    }
}
