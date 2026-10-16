using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// Signaling over TLS. Without it every device code, address and connect request travels in clear text, and an
/// on-path attacker can also take a relay ticket out of the traffic. The settings checked here are the ones
/// docs/ops/README.md tells an operator to use, so this is what keeps that page honest.
/// </summary>
public class TlsSignalingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_documented_settings_serve_signaling_over_wss()
    {
        var folder = Directory.CreateTempSubdirectory("rd-tls-");
        try
        {
            const string Password = "test-only";
            var (pfx, thumbprint) = CreateServerCertificate(Password);
            var path = Path.Combine(folder.FullName, "signaling.pfx");
            await File.WriteAllBytesAsync(path, pfx, Ct);

            // Exactly the shape the deployment page hands to an operator: a URL and a certificate file.
            await using var server = await TestRendezvous.StartAsync(
                ("Urls", "https://127.0.0.1:0"),
                ("Kestrel:Certificates:Default:Path", path),
                ("Kestrel:Certificates:Default:Password", Password));

            var uri = TestRendezvous.SignalingUri(server, scheme: "wss");

            // The client is a real one and validates the chain; a deployment's certificate is trusted by the machine,
            // and this one is only trusted here, by its thumbprint.
            using var socket = await RawSignaling.ConnectAsync(
                uri,
                (_, presented, _, _) => presented is not null && presented.GetCertHashString() == thumbprint);

            Assert.Equal(WebSocketState.Open, socket.State);

            // And the handshake works on top of it, not just the connection.
            Assert.NotNull(await RawSignaling.RegisterAsync(socket));
        }
        finally
        {
            Directory.Delete(folder.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task A_client_that_does_not_trust_the_certificate_is_turned_away()
    {
        var folder = Directory.CreateTempSubdirectory("rd-tls-");
        try
        {
            var (pfx, _) = CreateServerCertificate("test-only");
            var path = Path.Combine(folder.FullName, "signaling.pfx");
            await File.WriteAllBytesAsync(path, pfx, Ct);

            await using var server = await TestRendezvous.StartAsync(
                ("Urls", "https://127.0.0.1:0"),
                ("Kestrel:Certificates:Default:Path", path),
                ("Kestrel:Certificates:Default:Password", "test-only"));

            // A self-signed certificate is not a chain the machine trusts, so the default validation — which is what
            // the product's own client uses — refuses it. That is the point of the check, not an obstacle to it.
            await Assert.ThrowsAnyAsync<WebSocketException>(
                () => RawSignaling.ConnectAsync(TestRendezvous.SignalingUri(server, scheme: "wss")));
        }
        finally
        {
            Directory.Delete(folder.FullName, recursive: true);
        }
    }

    /// <summary>
    /// A self-signed certificate for loopback, shaped like one an operator would install: the file the server reads
    /// and the thumbprint a client would see. Exported from the original, whose key can still leave the process.
    /// </summary>
    private static (byte[] Pfx, string Thumbprint) CreateServerCertificate(string password)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return (certificate.Export(X509ContentType.Pfx, password), certificate.Thumbprint);
    }
}
