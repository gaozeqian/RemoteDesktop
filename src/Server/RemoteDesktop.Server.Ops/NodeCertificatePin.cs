using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace RemoteDesktop.Server.Ops;

/// <summary>
/// The HTTP handler for calls to nodes: the platform's normal validation, or exactly one pinned certificate when one
/// is configured.
/// </summary>
/// <remarks>
/// The server package writes the signaling certificate here when signaling uses TLS. macOS refuses
/// <c>security add-trusted-cert</c> from a package's postinstall ("authorization was denied since no user interaction
/// was possible"), so the console cannot rely on the System keychain for its own self-signed certificate. Only the
/// two node clients use this handler; alert webhooks keep the default validation.
/// </remarks>
internal static class NodeCertificatePin
{
    /// <summary>
    /// Fails at start-up when the configured pin cannot be read, rather than on every scrape. A bad path used to throw
    /// inside the scraper's handler factory after the host was already serving, which the background worker could not
    /// recover from.
    /// </summary>
    public static void Validate(string? certificatePath)
    {
        if (string.IsNullOrWhiteSpace(certificatePath))
        {
            return;
        }

        if (!File.Exists(certificatePath))
        {
            throw new InvalidOperationException($"Ops:NodeCertificatePath '{certificatePath}' does not exist.");
        }

        try
        {
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Ops:NodeCertificatePath '{certificatePath}' could not be read as a certificate: {ex.Message}",
                ex);
        }
    }

    public static HttpMessageHandler Handler(string? certificatePath)
    {
        var handler = new SocketsHttpHandler();
        if (string.IsNullOrWhiteSpace(certificatePath))
        {
            return handler;
        }

        byte[] pinned;
        using (var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath))
        {
            pinned = certificate.GetRawCertData();
        }

        handler.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, _) =>
            presented is not null && presented.GetRawCertData().AsSpan().SequenceEqual(pinned);
        return handler;
    }
}
