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
                return new DeviceIdentity(Import(stored));
            }
            catch (CryptographicException)
            {
                // Corrupt or unreadable: fall through and create a new identity (the device gets a new id).
            }
        }

        var pfx = CreatePfx();
        store.Save(SecretName, pfx);
        return new DeviceIdentity(Import(pfx));
    }

    /// <summary>An identity that is not persisted (tests, one-off tools).</summary>
    public static DeviceIdentity CreateEphemeral() => new(Import(CreatePfx()));

    /// <summary>
    /// Loads a PKCS#12 the way the QUIC stack needs it: the private key has to be exportable, because MsQuic takes
    /// the key out of the certificate to hand it to the TLS layer, and a key that cannot be exported fails there with
    /// an error that says nothing about certificates. On some systems the default lands the key in a store that will
    /// not give it up — macOS and its keychain is the documented case — and asking up front costs nothing anywhere
    /// else, including Windows, where it is ignored.
    /// </summary>
    private static X509Certificate2 Import(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.Exportable);

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
