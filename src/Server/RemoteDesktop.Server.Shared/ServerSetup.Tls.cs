using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace RemoteDesktop.Server.Shared;

public static partial class ServerSetup
{
    /// <summary>
    /// What the signaling server listens on. A certificate switches it to https on its own: an installer that was
    /// given one was asked for TLS, and asking for TLS and serving http is not something to do quietly.
    /// </summary>
    private static string? SignalingUrl(
        string? configured,
        string? existingUrls,
        string? certificatePath,
        List<string> notes)
    {
        // Null means "leave the address alone". A certificate may still change the scheme: configure-server.sh
        // documents --tls-certificate as "serve TLS with this certificate", and nobody should have to repeat the
        // address to turn https on. Anything else (including an explicit "auto" or "") is a value for this run.
        if (configured is null)
        {
            if (IsAuto(certificatePath))
            {
                return null;
            }

            var current = string.IsNullOrWhiteSpace(existingUrls)
                ? $"http://127.0.0.1:{DefaultSignalingPort}"
                : existingUrls;
            if (!current.StartsWith("http://", StringComparison.Ordinal)
                && !current.StartsWith("https://", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The existing signaling address must start with http:// or https:// (got '{current}').");
            }

            return WithCertificate(current, certificatePath, notes);
        }

        var urls = Expand(configured);
        if (IsAuto(urls))
        {
            // The secure default: a server whose operator did not choose an interface is reachable only from this
            // machine. A public listener is an explicit choice (https://, or http:// plus AllowInsecureSignaling).
            urls = $"http://127.0.0.1:{DefaultSignalingPort}";
        }

        if (!urls.StartsWith("http://", StringComparison.Ordinal)
            && !urls.StartsWith("https://", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The signaling address must start with http:// or https:// (got '{urls}').");
        }

        return WithCertificate(urls, certificatePath, notes);
    }

    /// <summary>
    /// Applies the certificate choice to an address. A certificate turns an http address into https; without one the
    /// address is left on http and the consequence is said out loud.
    /// </summary>
    private static string WithCertificate(string urls, string? certificatePath, List<string> notes)
    {
        if (!Uri.TryCreate(urls, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https")
            || parsed.Host.Length == 0)
        {
            throw new InvalidOperationException(
                $"The signaling address must be an http:// or https:// address with a host (got '{urls}').");
        }

        if (urls.StartsWith("https://", StringComparison.Ordinal))
        {
            if (IsAuto(certificatePath))
            {
                notes.Add("The signaling address is https but no certificate was given: the server will not start until Kestrel:Certificates:Default is filled in by hand.");
            }

            return urls;
        }

        // "auto" is the installer's default property value, and it means the same as an empty one here: no certificate.
        if (IsAuto(certificatePath))
        {
            notes.Add("Signaling is served over plain http: anyone on the way can read the device codes and take a relay ticket. Give the installer a certificate (see docs/ops/README.md) or put a reverse proxy in front.");
            return urls;
        }

        if (certificatePath != ServerSetupOptions.GenerateCertificate && !File.Exists(certificatePath))
        {
            notes.Add($"The certificate '{certificatePath}' is not there, so the server was left on plain http. Put the file in place and set Kestrel:Certificates:Default by hand, or run this step again.");
            return urls;
        }

        // Either a file that is there, or the certificate this step is about to make.
        return "https://" + urls["http://".Length..];
    }

    /// <summary>The extra names a generated certificate has to answer, split on the separators an operator is likely to type.</summary>
    private static string[] AdditionalNames(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Loads the certificate the way Kestrel will, so a wrong password, a missing key or a file that is not a
    /// certificate fails the installation instead of leaving a server that cannot start.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file cannot be used as a server certificate.</exception>
    private static void ValidateCertificateFile(string certificatePath, string? password)
    {
        var secret = IsAuto(password) ? string.Empty : password!;

        try
        {
            if (Path.GetExtension(certificatePath) is ".pfx" or ".p12")
            {
                // macOS has no ephemeral key set; asking for one throws PlatformNotSupportedException before the
                // certificate is even read. DefaultKeySet is what Kestrel itself does on this platform, so the
                // validation stays "the same load the server will perform" rather than a weaker check.
                var flags = OperatingSystem.IsMacOS()
                    ? X509KeyStorageFlags.DefaultKeySet
                    : X509KeyStorageFlags.EphemeralKeySet;

                using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                    certificatePath,
                    secret,
                    flags);

                if (!certificate.HasPrivateKey)
                {
                    throw new InvalidOperationException($"The certificate '{certificatePath}' has no private key, so the server cannot serve TLS with it.");
                }

                return;
            }

            var keyPath = KeyBeside(certificatePath)
                ?? throw new InvalidOperationException(
                    $"The certificate '{certificatePath}' is not a PKCS#12 file and has no key beside it. A PEM certificate needs its key in a file with the same name and a .key extension ({Path.ChangeExtension(certificatePath, ".key")}).");

            using var pair = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
            if (!pair.HasPrivateKey)
            {
                throw new InvalidOperationException($"The certificate '{certificatePath}' and '{keyPath}' do not form a key pair, so the server cannot serve TLS with them.");
            }
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException($"The certificate '{certificatePath}' could not be read: {ex.Message}", ex);
        }
    }

}
