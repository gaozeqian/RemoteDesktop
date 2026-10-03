using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteDesktop.Abstractions.Platform;

namespace RemoteDesktop.Transport.Identity;

/// <summary>
/// The device's long-lived key pair, as a self-signed certificate. Its SHA-256 fingerprint is what peers pin during
/// the TLS handshake and what the rendezvous server maps to the device id.
/// </summary>
public sealed class DeviceIdentity : IDisposable
{
    public const string SecretName = "device-identity";

    private DeviceIdentity(X509Certificate2 certificate)
    {
        Certificate = certificate;
        Fingerprint = ComputeFingerprint(certificate);
    }

    /// <summary>Certificate with private key, usable as TLS server and client certificate.</summary>
    public X509Certificate2 Certificate { get; }

    public string Fingerprint { get; }

    public static DeviceIdentity LoadOrCreate(ISecretStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        var stored = store.Load(SecretName);
        if (stored is not null)
        {
            try
            {
                return new DeviceIdentity(X509CertificateLoader.LoadPkcs12(stored, password: null));
            }
            catch (CryptographicException)
            {
                // Corrupt or unreadable: fall through and create a new identity (the device gets a new id).
            }
        }

        var pfx = CreatePfx();
        store.Save(SecretName, pfx);
        return new DeviceIdentity(X509CertificateLoader.LoadPkcs12(pfx, password: null));
    }

    /// <summary>An identity that is not persisted (tests, one-off tools).</summary>
    public static DeviceIdentity CreateEphemeral() => new(X509CertificateLoader.LoadPkcs12(CreatePfx(), password: null));

    public static string ComputeFingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }

    /// <summary>Signs <paramref name="data"/> to prove possession of the key (rendezvous registration).</summary>
    public byte[] Sign(ReadOnlySpan<byte> data)
    {
        using var key = Certificate.GetECDsaPrivateKey() ?? throw new InvalidOperationException("Identity has no private key.");
        return key.SignData(data, HashAlgorithmName.SHA256);
    }

    public void Dispose() => Certificate.Dispose();

    // Round-tripping through PFX gives a key that SChannel (used by MsQuic on Windows) accepts; a key created by
    // CreateSelfSigned alone is ephemeral and rejected for TLS server use.
    private static byte[] CreatePfx()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=RemoteDesktop Device", key, HashAlgorithmName.SHA256);
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));
        return certificate.Export(X509ContentType.Pfx);
    }
}
