using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Hosting;
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
    /// <param name="builder">The host builder.</param>
    /// <param name="databasePathBase">
    /// The directory a relative SQLite path is resolved against. The service passes the directory its live settings
    /// came from; a test or a plain <c>dotnet run</c> leaves it as the content root. Resolving the path here once, with
    /// the same helper the setup CLI uses, is what keeps <c>--create-admin</c> and the service on one database.
    /// </param>
    public static WebApplicationBuilder AddOps(this WebApplicationBuilder builder, string? databasePathBase = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Configuration.GetSection(OpsOptions.SectionName).Get<OpsOptions>() ?? new OpsOptions();
        options.Database ??= new OpsDatabaseSettings();

        // Absolute paths — what the Linux and macOS packages configure — are left as they are. A relative one is
        // resolved against the settings directory, not the process's current directory: a Windows service starts in
        // System32, and the setup CLI may run from anywhere. Expanding environment variables lets a packaged install
        // say %ProgramData%, which is right even on a machine whose data directory is not on C:.
        try
        {
            options.Database.Path = OpsDatabaseSettings.ResolvePath(
                options.Database.Provider,
                options.Database.Path,
                databasePathBase ?? builder.Environment.ContentRootPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException($"Ops:Database:Path is not a usable path: {ex.Message}", ex);
        }

        // Stopping at start-up beats serving a sign-in page that cannot work, or a background worker that faults on
        // every pass because a pin or webhook cannot be read. Each message names the key.
        options.Validate();
        ValidateIngestKeys(options);
        NodeCertificatePin.Validate(options.NodeCertificatePath);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(OpsStore.Open(options.Database));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<LoginThrottle>();
        builder.Services.AddSingleton<AddressThrottle>();
        builder.Services.AddSingleton<ChallengeStore>();
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddSingleton<OverviewService>();
        builder.Services.AddHostedService<OpsSchemaInitializer>();
        builder.Services.AddSingleton<AlertStateMachine>();
        builder.Services.AddSingleton<AlertService>();
        builder.Services.AddHostedService<AlertRuleSeeder>();
        builder.Services.AddSingleton<OpsLease>();

        // The last line of defence for a background pass that still throws: one bad scrape or webhook must not stop the
        // console. The individual loops catch and continue; this only decides what happens when one does not.
        builder.Services.Configure<HostOptions>(
            host => host.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

        // Registered as a singleton as well as a hosted service, so a test can drive one evaluation directly.
        builder.Services.AddSingleton<AlertEvaluationService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertEvaluationService>());

        // The scraper is a singleton as well as a hosted service so a test can drive one pass directly.
        builder.Services.AddHttpClient(MetricScraper.ClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.ScrapeTimeoutSeconds));
                client.MaxResponseContentBufferSize = NodeHttp.MaxResponseBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(() => NodeCertificatePin.Handler(options.NodeCertificatePath));
        builder.Services.AddHttpClient(DeviceEndpoints.ClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.ScrapeTimeoutSeconds));
                client.MaxResponseContentBufferSize = NodeHttp.MaxResponseBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(() => NodeCertificatePin.Handler(options.NodeCertificatePath));
        builder.Services.AddHttpClient(AlertService.ClientName, client => client.Timeout = TimeSpan.FromSeconds(5));
        builder.Services.AddSingleton<MetricScraper>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MetricScraper>());
        builder.Services.AddHostedService<RetentionService>();
        builder.Services.AddHostedService<ActionRecoveryService>();

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

        // Before anything reads RemoteIpAddress or Request.IsHttps. The framework's trust of loopback proxies is
        // removed: a proxy on this machine is named in Ops:TrustedProxies like any other, so a local caller cannot
        // choose its own audit address or make a cookie Secure by writing a header.
        app.UseForwardedHeaders(ForwardedHeaderOptions(app));

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
        app.MapOpsUsers();
        app.MapOpsIngest();

        // Anything else under /api is a 404, not the single-page shell: a JSON client that receives HTML has to parse
        // its way to the same conclusion, badly.
        app.Map("/api/{**rest}", () => Results.NotFound());
        app.MapFallbackToFile("index.html");

        return app;
    }

    /// <summary>
    /// The proxies whose forwarded headers are believed. The defaults are cleared first: keeping the framework's
    /// loopback trust would let anything that can reach the local socket choose the address the audit log records and
    /// the address the sign-in throttle counts against.
    /// </summary>
    private static ForwardedHeadersOptions ForwardedHeaderOptions(WebApplication app)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1,
        };

        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var entry in app.Services.GetRequiredService<OpsOptions>().TrustedProxies)
        {
            // Validated at start-up, so both parses are known to succeed here.
            if (IPAddress.TryParse(entry, out var address))
            {
                options.KnownProxies.Add(address);
            }
            else if (System.Net.IPNetwork.TryParse(entry, out var network))
            {
                options.KnownIPNetworks.Add(network);
            }
        }

        return options;
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
            catch (Exception ex) when (ex is FormatException or ArgumentNullException)
            {
                throw new InvalidOperationException($"Ops:Ingest:Keys:{id} must be base64 (generate one with: openssl rand -base64 48).", ex);
            }

            if (key.Length < 32)
            {
                throw new InvalidOperationException($"Ops:Ingest:Keys:{id} must be at least 32 bytes (it is {key.Length}).");
            }
        }
    }
}
