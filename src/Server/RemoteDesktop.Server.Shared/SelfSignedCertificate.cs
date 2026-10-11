using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteDesktop.Server.Shared;

/// <summary>
/// Makes the certificate the signaling server serves TLS with, for a deployment that has no certificate of its own.
/// </summary>
/// <remarks>
/// <para>
/// This is the only certificate the product creates by itself. It exists because the alternative for a small
/// deployment is plain http, and over plain http the device codes and the relay tickets travel in the clear — that is
/// a real price, and "run one command to get https" is worth more than a paragraph telling the operator to go and
/// learn about certificate authorities.
/// </para>
/// <para>
/// The private key never leaves the machine's own certificate store. The installation folder is readable by every user
/// on the machine (it lives under Program Files), so a key file next to the settings would be readable too; what is
/// written there is only the public half, which is what a client has to trust.
/// </para>
/// </remarks>
public static class SelfSignedCertificate
{
    /// <summary>The store the generated certificate lives in, and what Kestrel is pointed at.</summary>
    public const string CertificateStoreName = "My";

    /// <summary>Where it lives: the machine, not the user who happened to run the installer.</summary>
    public const string CertificateStoreLocation = "LocalMachine";

    private const string CommonNamePrefix = "RemoteDesktop Server";

    /// <summary>The subject this machine's certificate is created under.</summary>
    public static string SubjectForThisMachine => $"CN={CommonNamePrefix} ({Environment.MachineName})";

    private static readonly TimeSpan Validity = TimeSpan.FromDays(3 * 365);

    /// <summary>
    /// The subject of this machine's certificate, creating it when it is not there yet.
    /// </summary>
    /// <param name="installDirectory">Where the public half is written for clients to be given.</param>
    /// <param name="notes">One line per thing that happened, for whoever is watching the installation.</param>
    /// <returns>The subject, which is what the settings file refers to the certificate by.</returns>
    /// <exception cref="CryptographicException">The certificate could not be created or stored.</exception>
    /// <exception cref="UnauthorizedAccessException">Not allowed to write to the machine's certificate store.</exception>
    public static string Ensure(string installDirectory, List<string> notes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentNullException.ThrowIfNull(notes);

        var subject = SubjectForThisMachine;
        var publicPath = Path.Combine(installDirectory, PublicFolder, PublicFileName);

        using var store = new X509Store(CertificateStoreName, System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);

        var existing = store.Certificates
            .Where(certificate => certificate.Subject == subject && certificate.HasPrivateKey)
            .OrderByDescending(certificate => certificate.NotAfter)
            .FirstOrDefault();

        if (existing is not null)
        {
            // Kept, not replaced: a client that trusted this certificate yesterday must still trust it today, and
            // reinstalling or upgrading the server runs this step again.
            WritePublicHalf(installDirectory, publicPath, existing);
            notes.Add($"Signaling TLS uses the existing certificate '{subject}' from {CertificateStoreLocation}\\{CertificateStoreName}; its public half is at {publicPath}.");
            return subject;
        }

        var created = CreateForThisMachine(subject);
        store.Add(created);
        WritePublicHalf(installDirectory, publicPath, created);

        notes.Add($"A self-signed certificate '{subject}' was created and stored in {CertificateStoreLocation}\\{CertificateStoreName} (valid until {created.NotAfter:yyyy-MM-dd}).");
        notes.Add($"Clients must trust it, or they will refuse the connection: give them {publicPath} (Import-Certificate -FilePath ... -CertStoreLocation Cert:\\LocalMachine\\Root), or put a certificate from a real authority in Kestrel:Certificates:Default instead.");

        return subject;
    }

    /// <summary>The names this machine answers to, which is what the certificate has to cover.</summary>
    private static SubjectAlternativeNameBuilder AlternativeNames()
    {
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(Environment.MachineName);

        var host = Dns.GetHostName();
        if (!string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            names.AddDnsName(host);
        }

        try
        {
            foreach (var address in Dns.GetHostAddresses(host))
            {
                // IPv4 only: what the rest of the product speaks (the relay endpoint is an IPv4 address or a name).
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    names.AddIpAddress(address);
                }
            }
        }
        catch (SocketException)
        {
            // A machine whose own name does not resolve still gets a usable certificate; the name is in there.
        }

        return names;
    }

    /// <summary>
    /// The certificate itself, for this machine, without going near the certificate store.
    /// </summary>
    /// <remarks>
    /// Public so the shape of what gets created can be tested without elevation: the tests check that the machine's
    /// names are in there, that it is a server certificate, and that the private key survives the round trip through
    /// PKCS#12 — a certificate straight out of <c>CreateSelfSigned</c> has a key that lives only as long as the RSA
    /// object, and a store cannot keep that one.
    /// </remarks>
    public static X509Certificate2 CreateForThisMachine(string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // serverAuth
        request.CertificateExtensions.Add(AlternativeNames().Build());

        using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.Add(Validity));

        // Through PKCS#12 and back: a certificate straight out of CreateSelfSigned has a key that only lives as long as
        // the RSA object, and the store cannot keep that one. Importing with PersistKeySet is what makes the key stay.
        return X509CertificateLoader.LoadPkcs12(
            selfSigned.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
    }

    private static void WritePublicHalf(string installDirectory, string publicPath, X509Certificate2 certificate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(publicPath)!);
        File.WriteAllBytes(publicPath, certificate.Export(X509ContentType.Cert));
    }

    /// <summary>Where the public half goes, next to the settings an operator is already told to look at.</summary>
    private const string PublicFolder = "tls";

    private const string PublicFileName = "signaling.cer";
}
