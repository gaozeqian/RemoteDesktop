using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// <c>Transport:RendezvousCertificatePath</c> is read when the host starts: a pin that cannot be read is a startup
/// error with a sentence, not a connection that silently falls back to (or never gets past) platform validation.
/// </summary>
public sealed class TransportCertificatePinValidationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rd-pin-validate-{Guid.NewGuid():N}");

    public TransportCertificatePinValidationTests() => Directory.CreateDirectory(_directory);

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
    public void A_pin_that_does_not_exist_is_a_start_up_error_naming_the_setting_and_the_file()
    {
        var path = Path.Combine(_directory, "missing.cer");
        var options = Secure(path);

        var error = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("Transport:RendezvousCertificatePath", error.Message, StringComparison.Ordinal);
        Assert.Contains(path, error.Message, StringComparison.Ordinal);
        Assert.Contains("could not be read", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public void A_pin_that_is_not_a_certificate_is_a_start_up_error()
    {
        var path = Path.Combine(_directory, "garbage.cer");
        File.WriteAllText(path, "this is not a certificate");

        var error = Assert.Throws<InvalidOperationException>(Secure(path).Validate);

        Assert.Contains("could not be read", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_directory_given_as_the_pin_is_a_start_up_error()
    {
        var error = Assert.Throws<InvalidOperationException>(Secure(_directory).Validate);

        Assert.Contains("could not be read", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_readable_certificate_file_is_accepted_as_der_or_pem(bool pem)
    {
        var path = WriteCertificate(pem ? "pin.pem" : "pin.cer", pem);

        Secure(path).Validate();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_pin_means_the_platform_decides_and_is_not_an_error(string? path) => Secure(path).Validate();

    /// <summary>
    /// A pin left behind after the address was changed to ws:// is harmless - there is no TLS to validate - so it is
    /// not read at all and cannot stop the host from starting.
    /// </summary>
    [Fact]
    public void A_pin_is_not_read_for_a_plaintext_address()
    {
        var options = new TransportOptions
        {
            RendezvousUri = new Uri("ws://127.0.0.1:5080/ws"),
            RendezvousCertificatePath = Path.Combine(_directory, "missing.cer"),
        };

        options.Validate();
    }

    private static TransportOptions Secure(string? pinPath) =>
        new() { RendezvousUri = new Uri("wss://rd.example.com/ws"), RendezvousCertificatePath = pinPath };

    private string WriteCertificate(string name, bool pem)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        var path = Path.Combine(_directory, name);
        if (pem)
        {
            File.WriteAllText(path, certificate.ExportCertificatePem());
        }
        else
        {
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Cert));
        }

        return path;
    }
}
