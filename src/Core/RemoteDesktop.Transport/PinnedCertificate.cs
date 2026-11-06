using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace RemoteDesktop.Transport;

/// <summary>
/// Turns a certificate file into the validation callback that accepts exactly that certificate and nothing else.
/// </summary>
/// <remarks>
/// Used for the signaling server's self-signed certificate. It deliberately does not fall back to "whatever the
/// platform trusts": a pin that also accepts every public certificate is a pin in name only, and the whole reason the
/// installer carries the server's public half is that this deployment's certificate is not from a public authority.
/// </remarks>
internal static class PinnedCertificate
{
    /// <returns><c>null</c> when no certificate was configured, so the caller leaves the default validation alone.</returns>
    public static RemoteCertificateValidationCallback? TryCreate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        byte[] pinned;
        using (var certificate = X509CertificateLoader.LoadCertificateFromFile(path))
        {
            pinned = certificate.GetRawCertData();
        }

        return (_, presented, _, _) =>
            presented is not null && presented.GetRawCertData().AsSpan().SequenceEqual(pinned);
    }
}
