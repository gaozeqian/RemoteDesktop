using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RemoteDesktop.Server.Ops.Core;

/// <summary>
/// RFC 6238 time-based one-time passwords, in the profile every authenticator app implements: HMAC-SHA1, six digits,
/// thirty-second steps.
/// </summary>
/// <remarks>
/// SHA-1 is not a choice here. RFC 6238's interoperability profile is HMAC-SHA1 and apps that support the others only
/// do so when the enrolment URI names them, so switching would lock out every already-enrolled phone. The HMAC is a
/// PRF, not a collision-resistant hash use, which is why the old digest is still the right primitive for this one job.
/// </remarks>
public static class Totp
{
    /// <summary>Seconds per code.</summary>
    public const int StepSeconds = 30;

    /// <summary>Digits per code.</summary>
    public const int Digits = 6;

    private const int SecretBytes = 20;

    /// <summary>A fresh Base32 secret: 160 bits, the length RFC 4226 recommends for HMAC-SHA1.</summary>
    public static string GenerateSecret() => Base32.Encode(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>The code for <paramref name="at"/>.</summary>
    public static string Compute(string secret, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        return ComputeAtCounter(secret, at.ToUnixTimeSeconds() / StepSeconds);
    }

    /// <summary>
    /// True when <paramref name="code"/> is the code for <paramref name="at"/>, or for the step before or after it —
    /// clocks drift, and a phone that is thirty seconds fast should not lock its owner out.
    /// </summary>
    public static bool Verify(string secret, string code, DateTimeOffset at, int window = 1) =>
        TryVerify(secret, code, at, out _, window);

    /// <summary>
    /// Verifies a code and reports which time step it matched. The step is what makes a code single-use: the caller
    /// stores it and refuses a later sign-in that presents the same step again.
    /// </summary>
    public static bool TryVerify(string secret, string code, DateTimeOffset at, out long step, int window = 1)
    {
        step = 0;
        if (string.IsNullOrEmpty(secret) || code.Length != Digits)
        {
            return false;
        }

        foreach (var character in code)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        var counter = at.ToUnixTimeSeconds() / StepSeconds;
        var provided = Encoding.ASCII.GetBytes(code);
        for (var offset = -window; offset <= window; offset++)
        {
            var expected = Encoding.ASCII.GetBytes(ComputeAtCounter(secret, counter + offset));
            if (CryptographicOperations.FixedTimeEquals(expected, provided))
            {
                step = counter + offset;
                return true;
            }
        }

        return false;
    }

    /// <summary>The <c>otpauth://</c> URI an authenticator app scans or accepts pasted.</summary>
    public static string BuildUri(string issuer, string account, string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(issuer);
        ArgumentException.ThrowIfNullOrEmpty(account);
        ArgumentException.ThrowIfNullOrEmpty(secret);

        var label = Uri.EscapeDataString(issuer) + ":" + Uri.EscapeDataString(account);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"otpauth://totp/{label}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}");
    }

    private static string ComputeAtCounter(string secret, long counter)
    {
        var key = Base32.Decode(secret);
        Span<byte> counterBytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);

#pragma warning disable CA5350 // RFC 6238's interoperability profile is HMAC-SHA1; see the type's remarks.
        var mac = HMACSHA1.HashData(key, counterBytes);
#pragma warning restore CA5350

        var offset = mac[^1] & 0x0F;
        var binary = ((mac[offset] & 0x7F) << 24)
            | (mac[offset + 1] << 16)
            | (mac[offset + 2] << 8)
            | mac[offset + 3];

        var modulus = 1;
        for (var i = 0; i < Digits; i++)
        {
            modulus *= 10;
        }

        return (binary % modulus).ToString("D6", CultureInfo.InvariantCulture);
    }
}
