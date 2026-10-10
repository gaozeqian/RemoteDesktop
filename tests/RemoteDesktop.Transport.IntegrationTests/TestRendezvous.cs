using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Server.Rendezvous;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// A rendezvous server on loopback, configured by the test that starts it. The shared fixture is one of these plus a
/// relay; a test that needs limits the fixture must not have — or a server speaking TLS — starts its own.
/// </summary>
public static class TestRendezvous
{
    public static async Task<WebApplication> StartAsync(params (string Key, string? Value)[] settings)
    {
        var builder = WebApplication.CreateSlimBuilder();

        // The slim builder leaves HTTPS out; the shipped server uses the full one and has it. A test that asks for
        // https:// endpoints means it.
        builder.WebHost.UseKestrelHttpsConfiguration();

        // By default a loopback port nobody has to pick. A test that sets Urls is asking for endpoints of its own —
        // the TLS test does, because that is the setting a deployment is told to use.
        if (!settings.Any(setting => string.Equals(setting.Key, "Urls", StringComparison.OrdinalIgnoreCase)))
        {
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        }

        // A fixed reflector port would have every server in a test run fighting over the same one, so the default is
        // a free port and a caller that cares can still say otherwise.
        var configuration = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Rendezvous:ReflectorPort"] = "0",
        };
        foreach (var (key, value) in settings)
        {
            configuration[key] = value;
        }

        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Services.AddRendezvous(builder.Configuration);

        var app = builder.Build();
        app.MapRendezvous();
        await app.StartAsync();
        return app;
    }

    /// <summary>The signaling address of a server started above, with the scheme the caller asks for.</summary>
    public static Uri SignalingUri(WebApplication app, string scheme = "ws") =>
        new($"{scheme}://127.0.0.1:{new Uri(app.Urls.First()).Port}{RendezvousServer.SignalingPath}");
}
