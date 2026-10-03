using System.Security.Cryptography;

namespace RemoteDesktop.Application.Security;

/// <summary>Generates the short-lived password shown next to the device id.</summary>
public static class TemporaryPasswordGenerator
{
    // No 0/O, 1/l/I: the password is read aloud and typed by hand.
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    public const int DefaultLength = 8;

    public static string Generate(int length = DefaultLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 6);
        return RandomNumberGenerator.GetString(Alphabet, length);
    }
}
