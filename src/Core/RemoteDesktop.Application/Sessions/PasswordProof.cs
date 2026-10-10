using System.Security.Cryptography;
using System.Text;

namespace RemoteDesktop.Application.Sessions;

/// <summary>
/// Proof that the controller knows the host's password, without sending the password. The proof is bound to the
/// host's nonce and to both TLS certificate fingerprints, so it cannot be replayed on another connection or relayed
/// by a man in the middle (who would present a different certificate).
/// </summary>
/// <remarks>
/// <para>
/// This is not a PAKE: whoever completes the TLS handshake as the host — a hostile rendezvous server, or anybody on
/// the path while signaling is unencrypted — holds a verifier and can test passwords against it offline. Two things
/// keep that from being cheap, and neither is a substitute for replacing this with SPAKE2 or OPAQUE:
/// </para>
/// <para>
/// The password is put through <see cref="KeyDerivationIterations"/> rounds of PBKDF2 before it becomes a key, so
/// every guess an attacker makes costs what it costs this machine to answer one connection. And the nonce, which is
/// fresh for every connection, is the salt, so nothing an attacker derives for one connection carries over to the
/// next.
/// </para>
/// <para>
/// The cost is paid on both sides of every connection attempt: the host derives once per password it knows (the
/// temporary one and the fixed one), which is bounded by the same rate limiter that bounds the guesses themselves.
/// </para>
/// </remarks>
public static class PasswordProof
{
    public const int NonceSize = 32;

    /// <summary>
    /// How much work turns the password into the key the proof is made with. Both ends must agree on this number, so
    /// changing it changes the protocol: see <see cref="RemoteDesktop.Protocol.ProtocolVersion"/>, whose major was
    /// raised when this was introduced.
    /// </summary>
    public const int KeyDerivationIterations = 600_000;

    private static readonly byte[] Context = "rd-auth-v1"u8.ToArray();

    public static byte[] Compute(string password, ReadOnlySpan<byte> nonce, string hostFingerprint, string controllerFingerprint)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(hostFingerprint);
        ArgumentNullException.ThrowIfNull(controllerFingerprint);

        var key = Rfc2898DeriveBytes.Pbkdf2(password, nonce, KeyDerivationIterations, HashAlgorithmName.SHA256, SHA256.HashSizeInBytes);
        try
        {
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
            hmac.AppendData(Context);
            hmac.AppendData(nonce);
            hmac.AppendData(Encoding.ASCII.GetBytes(hostFingerprint.ToUpperInvariant()));
            hmac.AppendData(Encoding.ASCII.GetBytes(controllerFingerprint.ToUpperInvariant()));
            return hmac.GetHashAndReset();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static bool Verify(
        ReadOnlySpan<byte> proof,
        string password,
        ReadOnlySpan<byte> nonce,
        string hostFingerprint,
        string controllerFingerprint) =>
        CryptographicOperations.FixedTimeEquals(proof, Compute(password, nonce, hostFingerprint, controllerFingerprint));
}
