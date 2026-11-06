using System.Security.Cryptography;

namespace RemoteDesktop.Application.Security;

/// <summary>Generates the short-lived password shown next to the device id.</summary>
public static class TemporaryPasswordGenerator
{
    // No 0/O, 1/l/I: the password is read aloud and typed by hand.
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>
    /// Twelve characters of that alphabet, about 2^60. SRP removes the captured-proof/offline-cracking path, but an
    /// active fake host can still try one password per handshake: eight characters is 2^40 and too small for a
    /// long-lived credential, while twelve is read aloud as three groups of four and the window has a copy button.
    /// </summary>
    public const int DefaultLength = 12;

    public static string Generate(int length = DefaultLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 6);
        return RandomNumberGenerator.GetString(Alphabet, length);
    }
}
