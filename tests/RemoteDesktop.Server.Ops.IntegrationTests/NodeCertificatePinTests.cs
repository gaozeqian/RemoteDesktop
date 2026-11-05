using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// <c>Ops:NodeCertificatePath</c>: calls to nodes accept exactly the one pinned certificate, or the platform's normal
/// validation when nothing is pinned. The generated certificates the server packages use are self-signed, so "accept the
/// pinned one" and "refuse a different self-signed one" are the two halves of the promise.
/// </summary>
public sealed class NodeCertificatePinTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ops-pin-{Guid.NewGuid():N}");

    public NodeCertificatePinTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Cleaned by the OS later.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task The_pinned_certificate_is_accepted()
    {
        using var served = SelfSigned();
        var pinPath = WritePublicHalf(served, "pinned.cer");
        await using var node = await TlsNode.StartAsync(served);

        using var client = new HttpClient(NodeCertificatePin.Handler(pinPath));

        Assert.Equal("ok", await client.GetStringAsync(node.Address, Ct));
    }

    [Fact]
    public async Task A_different_certificate_is_refused_even_though_it_is_also_self_signed()
    {
        using var served = SelfSigned();
        using var other = SelfSigned();
        var pinPath = WritePublicHalf(other, "other.cer");
        await using var node = await TlsNode.StartAsync(served);

        using var client = new HttpClient(NodeCertificatePin.Handler(pinPath));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(node.Address, Ct));
    }

    [Fact]
    public async Task With_no_pin_a_self_signed_certificate_falls_to_the_platforms_validation_and_is_refused()
    {
        using var served = SelfSigned();
        await using var node = await TlsNode.StartAsync(served);

        using var client = new HttpClient(NodeCertificatePin.Handler(string.Empty));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(node.Address, Ct));
    }

    [Fact]
    public void No_path_is_accepted_at_start_up()
    {
        NodeCertificatePin.Validate(null);
        NodeCertificatePin.Validate(string.Empty);
    }

    [Fact]
    public void A_path_that_does_not_exist_is_refused_at_start_up()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => NodeCertificatePin.Validate(Path.Combine(_directory, "missing.cer")));

        Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_a_certificate_is_refused_at_start_up()
    {
        var path = Path.Combine(_directory, "garbage.cer");
        File.WriteAllText(path, "this is not a certificate");

        var error = Assert.Throws<InvalidOperationException>(() => NodeCertificatePin.Validate(path));

        Assert.Contains("could not be read as a certificate", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_readable_certificate_is_accepted_at_start_up()
    {
        using var certificate = SelfSigned();

        NodeCertificatePin.Validate(WritePublicHalf(certificate, "good.cer"));
    }

    private string WritePublicHalf(X509Certificate2 certificate, string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Cert));
        return path;
    }

    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());

        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        // Windows' TLS stack needs a key it can find again, which a PFX round trip provides.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }

    /// <summary>A loopback HTTPS server that presents one certificate.</summary>
    private sealed class TlsNode : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private TlsNode(WebApplication app, Uri address)
        {
            _app = app;
            Address = address;
        }

        public Uri Address { get; }

        public static async Task<TlsNode> StartAsync(X509Certificate2 certificate)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));

            var app = builder.Build();
            app.MapGet("/", () => "ok");
            await app.StartAsync(Ct);

            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new TlsNode(app, new Uri(address));
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
