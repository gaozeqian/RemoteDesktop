using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using RemoteDesktop.Server.Ops.Auth;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops;

/// <summary>
/// Composition of the console, shared by <c>Program</c> and the integration tests. One place knows the order of the
/// middleware; a test that builds the host any other way is testing a host nobody deploys.
/// </summary>
public static class OpsServer
{
    /// <summary>Registers options, the store, the auth scheme and the policies.</summary>
    public static WebApplicationBuilder AddOps(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Configuration.GetSection(OpsOptions.SectionName).Get<OpsOptions>() ?? new OpsOptions();

        // A relative SQLite path is relative to the program's content root, which for a service is where the program
        // lives — not the directory the service manager happened to start it in. (A Windows service starts in System32,
        // so the old behaviour put the console's database there.) Expanding environment variables lets a packaged
        // install say %ProgramData%, which is right even on a machine whose data directory is not on C:. Absolute
        // paths — what the Linux and macOS packages configure — are left as they are.
        if (!string.IsNullOrWhiteSpace(options.Database.Path))
        {
            var databasePath = Environment.ExpandEnvironmentVariables(options.Database.Path);
            if (!Path.IsPathRooted(databasePath))
            {
                databasePath = Path.Combine(builder.Environment.ContentRootPath, databasePath);
            }

            options.Database.Path = databasePath;
        }

        // Stopping at start-up beats serving a sign-in page that cannot work. The message names Ops:Database:*.
        options.Database.Validate();
        ValidateIngestKeys(options);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(OpsStore.Open(options.Database));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<LoginThrottle>();
        builder.Services.AddSingleton<ChallengeStore>();
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddSingleton<OverviewService>();
        builder.Services.AddHostedService<OpsSchemaInitializer>();
        builder.Services.AddSingleton<AlertStateMachine>();
        builder.Services.AddSingleton<AlertService>();
        builder.Services.AddHostedService<AlertRuleSeeder>();

        // Registered as a singleton as well as a hosted service, so a test can drive one evaluation directly.
        builder.Services.AddSingleton<AlertEvaluationService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertEvaluationService>());

        // The scraper is a singleton as well as a hosted service so a test can drive one pass directly.
        builder.Services.AddHttpClient(MetricScraper.ClientName, client =>
                client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.ScrapeTimeoutSeconds)))
            .ConfigurePrimaryHttpMessageHandler(() => NodeCertificatePin.Handler(options.NodeCertificatePath));
        builder.Services.AddHttpClient(DeviceEndpoints.ClientName, client =>
                client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.ScrapeTimeoutSeconds)))
            .ConfigurePrimaryHttpMessageHandler(() => NodeCertificatePin.Handler(options.NodeCertificatePath));
        builder.Services.AddHttpClient(AlertService.ClientName, client => client.Timeout = TimeSpan.FromSeconds(5));
        builder.Services.AddSingleton<MetricScraper>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MetricScraper>());

        builder.Services
            .AddAuthentication(OpsAuth.Scheme)
            .AddScheme<AuthenticationSchemeOptions, OpsSessionAuthenticationHandler>(OpsAuth.Scheme, configureOptions: null);

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(OpsAuth.ViewerPolicy, policy => policy.RequireRole(OpsRoles.Viewer, OpsRoles.Operator, OpsRoles.Admin))
            .AddPolicy(OpsAuth.OperatorPolicy, policy => policy.RequireRole(OpsRoles.Operator, OpsRoles.Admin))
            .AddPolicy(OpsAuth.AdminPolicy, policy => policy.RequireRole(OpsRoles.Admin));

        return builder;
    }

    /// <summary>Maps the pipeline: headers, auth, CSRF, static shell, endpoints, health.</summary>
    public static WebApplication UseOps(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The shell loads nothing from anywhere else, so the policy can be this strict. Inline script and style are
        // excluded on purpose: a future page that needs them should be a decision, not an accident.
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] =
                "default-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'; object-src 'none'";
            await next(context).ConfigureAwait(false);
        });

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<CsrfMiddleware>();

        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapOpsAuth();
        app.MapOpsHealth();
        app.MapOpsVersion();
        app.MapOpsNodes();
        app.MapOpsOverview();
        app.MapOpsSessions();
        app.MapOpsDevices();
        app.MapOpsAlerts();
        app.MapOpsAudit();
        app.MapOpsActions();
        app.MapOpsIngest();

        // Anything else under /api is a 404, not the single-page shell: a JSON client that receives HTML has to parse
        // its way to the same conclusion, badly.
        app.Map("/api/{**rest}", () => Results.NotFound());
        app.MapFallbackToFile("index.html");

        return app;
    }

    /// <summary>
    /// Refuses ingest keys that cannot work before the first server tries to sign with one. A short key is refused for
    /// the same reason the relay refuses a short shared key: the deployment is one edit away from not being signed at
    /// all, and finding out at start-up is cheaper than finding out from the events that went missing.
    /// </summary>
    private static void ValidateIngestKeys(OpsOptions options)
    {
        foreach (var (id, value) in options.Ingest.Keys)
        {
            byte[] key;
            try
            {
                key = Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                throw new InvalidOperationException($"Ops:Ingest:Keys:{id} must be base64 (generate one with: openssl rand -base64 48).");
            }

            if (key.Length < 32)
            {
                throw new InvalidOperationException($"Ops:Ingest:Keys:{id} must be at least 32 bytes (it is {key.Length}).");
            }
        }
    }
}
