using System.Security.Cryptography;
using System.Text;

namespace RemoteDesktop.Application.Sessions;

/// <summary>
/// Proof that the controller knows the host's password, without sending the password. The proof is bound to the
/// host's nonce and to both TLS certificate fingerprints, so it cannot be replayed on another connection or relayed
/// by a man in the middle (who would present a different certificate).
/// </summary>
/// <remarks>
/// This is not a PAKE: whoever completes the TLS handshake as the host sees the proof and could brute-force a weak
/// password offline. Certificate pinning limits that to a compromised rendezvous server; replacing this with SPAKE2
/// or OPAQUE is part of the M8 hardening.
/// </remarks>
public static class PasswordProof
{
    public const int NonceSize = 32;

    private static readonly byte[] Context = "rd-auth-v1"u8.ToArray();

    public static byte[] Compute(string password, ReadOnlySpan<byte> nonce, string hostFingerprint, string controllerFingerprint)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(hostFingerprint);
        ArgumentNullException.ThrowIfNull(controllerFingerprint);

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(password));
        hmac.AppendData(Context);
        hmac.AppendData(nonce);
        hmac.AppendData(Encoding.ASCII.GetBytes(hostFingerprint.ToUpperInvariant()));
        hmac.AppendData(Encoding.ASCII.GetBytes(controllerFingerprint.ToUpperInvariant()));
        return hmac.GetHashAndReset();
    }

    public static bool Verify(
        ReadOnlySpan<byte> proof,
        string password,
        ReadOnlySpan<byte> nonce,
        string hostFingerprint,
        string controllerFingerprint) =>
        CryptographicOperations.FixedTimeEquals(proof, Compute(password, nonce, hostFingerprint, controllerFingerprint));
}
