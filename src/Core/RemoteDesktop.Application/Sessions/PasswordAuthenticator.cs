using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement.Srp;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;

namespace RemoteDesktop.Application.Sessions;

/// <summary>
/// One server-side SRP-6a challenge. It is consumed by exactly one <see cref="PasswordAuthenticator.VerifyHost"/> call.
/// </summary>
public sealed class HostPasswordChallenge
{
    private readonly Srp6Server _server;

    internal HostPasswordChallenge(byte[] salt, byte[] serverPublic, Srp6Server server)
    {
        Salt = salt;
        ServerPublic = serverPublic;
        _server = server;
    }

    /// <summary>Fresh random salt for this challenge; it is part of the verifier.</summary>
    public byte[] Salt { get; }

    /// <summary>The server's SRP public value (B).</summary>
    public byte[] ServerPublic { get; }

    internal Srp6Server Server => _server;
}

/// <summary>
/// One client-side SRP-6a response. The server is authenticated only after
/// <see cref="PasswordAuthenticator.VerifyServer"/> accepts its evidence.
/// </summary>
public sealed class PasswordAttempt
{
    private readonly Srp6Client _client;

    internal PasswordAttempt(byte[] clientPublic, byte[] clientEvidence, Srp6Client client)
    {
        ClientPublic = clientPublic;
        ClientEvidence = clientEvidence;
        _client = client;
    }

    /// <summary>The client's SRP public value (A).</summary>
    public byte[] ClientPublic { get; }

    /// <summary>The client's evidence (M1); it proves knowledge of the password without revealing it.</summary>
    public byte[] ClientEvidence { get; }

    internal Srp6Client Client => _client;
}

/// <summary>
/// Password authentication over the already pinned peer connection using SRP-6a (RFC 5054 group 3072, SHA-256).
/// </summary>
/// <remarks>
/// <para>
/// This replaces the former challenge/HMAC proof, which was not a PAKE: anybody who could complete TLS as the host
/// obtained a verifier and could test passwords offline. SRP is a balanced PAKE. A passive observer gets no verifier,
/// and an active fake host can test at most one guess per handshake instead of brute-forcing a captured proof.
/// </para>
/// <para>
/// The peer certificate fingerprints are part of the SRP identity, so a transcript cannot be replayed on another
/// connection or between different peers even though the group parameters are public and long-lived.
/// </para>
/// <para>
/// The cryptographic construction and group arithmetic come from BouncyCastle's reviewed SRP implementation; this
/// class only frames its inputs and binds them to the connection. It must not be reimplemented by hand.
/// </para>
/// </remarks>
public static class PasswordAuthenticator
{
    /// <summary>Length of the per-handshake SRP salt.</summary>
    public const int SaltSize = 32;

    // SRP public values for the 3072-bit group are 384 bytes. The ceilings leave room for an implementation that
    // includes a leading zero, while refusing to hand an arbitrarily large BigInteger to modular exponentiation.
    public const int MaxPublicValueSize = 512;
    private const int MaxEvidenceSize = 128;

    private static readonly Srp6GroupParameters Group = Srp6StandardGroups.rfc5054_3072;

    /// <summary>
    /// Creates one challenge per password candidate. The caller always supplies the same number of candidates so the
    /// message shape does not reveal whether a fixed password is configured.
    /// </summary>
    public static IReadOnlyList<HostPasswordChallenge> CreateChallenges(
        IReadOnlyList<string> passwords,
        string hostFingerprint,
        string controllerFingerprint)
    {
        ArgumentNullException.ThrowIfNull(passwords);
        ArgumentNullException.ThrowIfNull(hostFingerprint);
        ArgumentNullException.ThrowIfNull(controllerFingerprint);
        if (passwords.Count == 0)
        {
            throw new ArgumentException("At least one password candidate is required.", nameof(passwords));
        }

        var identity = Identity(hostFingerprint, controllerFingerprint);
        var challenges = new List<HostPasswordChallenge>(passwords.Count);
        foreach (var password in passwords)
        {
            if (password is null)
            {
                throw new ArgumentException("A password candidate cannot be null.", nameof(passwords));
            }

            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var verifier = new Srp6VerifierGenerator();
            verifier.Init(Group, new Sha256Digest());
            var value = verifier.GenerateVerifier(salt, identity, Encoding.UTF8.GetBytes(password));

            var server = new Srp6Server();
            server.Init(Group, value, new Sha256Digest(), new SecureRandom());
            var serverPublic = server.GenerateServerCredentials().ToByteArrayUnsigned();
            challenges.Add(new HostPasswordChallenge(salt, serverPublic, server));
        }

        return challenges;
    }

    /// <summary>
    /// Creates the client response to one challenge. The server is not authenticated until its evidence is verified.
    /// </summary>
    public static PasswordAttempt Respond(
        string password,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> serverPublic,
        string hostFingerprint,
        string controllerFingerprint)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(hostFingerprint);
        ArgumentNullException.ThrowIfNull(controllerFingerprint);
        if (salt.Length < SaltSize || serverPublic.IsEmpty || serverPublic.Length > MaxPublicValueSize)
        {
            throw new ArgumentException("The SRP challenge is malformed.", nameof(serverPublic));
        }

        var client = new Srp6Client();
        client.Init(Group, new Sha256Digest(), new SecureRandom());
        var identity = Identity(hostFingerprint, controllerFingerprint);
        var clientPublic = client
            .GenerateClientCredentials(salt.ToArray(), identity, Encoding.UTF8.GetBytes(password))
            .ToByteArrayUnsigned();

        // The call is needed for its effect on the client state, which the evidence is computed from. The returned
        // BigInteger is immutable and cannot be wiped (ToByteArrayUnsigned would only hand out a copy to zero).
        _ = client.CalculateSecret(new BigInteger(1, serverPublic.ToArray()));
        var clientEvidence = client.CalculateClientEvidenceMessage().ToByteArrayUnsigned();
        return new PasswordAttempt(clientPublic, clientEvidence, client);
    }

    /// <summary>
    /// Verifies one response against one challenge. A malformed public value or evidence is simply a failed
    /// authentication; it must not escape as an exception from the protocol layer.
    /// </summary>
    public static bool VerifyHost(
        HostPasswordChallenge challenge,
        ReadOnlySpan<byte> clientPublic,
        ReadOnlySpan<byte> clientEvidence,
        out byte[] serverEvidence)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        serverEvidence = [];
        if (clientPublic.IsEmpty || clientPublic.Length > MaxPublicValueSize
            || clientEvidence.IsEmpty || clientEvidence.Length > MaxEvidenceSize)
        {
            return false;
        }

        try
        {
            // Called for its effect on the server state; the secret itself is an immutable BigInteger that cannot be wiped.
            _ = challenge.Server.CalculateSecret(new BigInteger(1, clientPublic.ToArray()));

            if (!challenge.Server.VerifyClientEvidenceMessage(new BigInteger(1, clientEvidence.ToArray())))
            {
                return false;
            }

            serverEvidence = challenge.Server.CalculateServerEvidenceMessage().ToByteArrayUnsigned();
            return true;
        }
        catch (Exception ex) when (ex is CryptoException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Verifies the host's evidence (M2), the half of SRP that authenticates the host to the client.</summary>
    public static bool VerifyServer(PasswordAttempt attempt, ReadOnlySpan<byte> serverEvidence)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (serverEvidence.IsEmpty || serverEvidence.Length > MaxEvidenceSize)
        {
            return false;
        }

        try
        {
            return attempt.Client.VerifyServerEvidenceMessage(new BigInteger(1, serverEvidence.ToArray()));
        }
        catch (Exception ex) when (ex is CryptoException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static byte[] Identity(string hostFingerprint, string controllerFingerprint) =>
        Encoding.ASCII.GetBytes(
            $"rd-auth-srp-v1|{hostFingerprint.ToUpperInvariant()}|{controllerFingerprint.ToUpperInvariant()}");
}
