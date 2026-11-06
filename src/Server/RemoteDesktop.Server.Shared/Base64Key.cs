namespace RemoteDesktop.Server.Shared;

/// <summary>
/// The "base64, and long enough" check that the relay and Ops signing keys go through wherever they are read. The text of the
/// refusal belongs to the caller: each place names its own setting and says what to do about it in its own words.
/// </summary>
public static class Base64Key
{
    /// <summary>The shortest key, in bytes, that any of the servers accepts.</summary>
    public const int MinimumBytes = 32;

    /// <summary>Decodes <paramref name="value"/> and checks that it is at least <paramref name="minimumBytes"/> long.</summary>
    /// <param name="value">The base64 text. A <see langword="null"/> is not caught here: it surfaces as <see cref="ArgumentNullException"/>, as with <see cref="Convert.FromBase64String"/>.</param>
    /// <param name="notBase64Message">The message of the <see cref="InvalidOperationException"/> thrown when the text is not base64.</param>
    /// <param name="tooShortMessage">Builds the message of the <see cref="InvalidOperationException"/> thrown for a short key, from its length in bytes.</param>
    /// <param name="minimumBytes">The shortest acceptable key.</param>
    /// <returns>The decoded key.</returns>
    public static byte[] Decode(string value, string notBase64Message, Func<int, string> tooShortMessage, int minimumBytes = MinimumBytes)
    {
        ArgumentNullException.ThrowIfNull(notBase64Message);
        ArgumentNullException.ThrowIfNull(tooShortMessage);

        byte[] key;
        try
        {
            key = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(notBase64Message);
        }

        if (key.Length < minimumBytes)
        {
            throw new InvalidOperationException(tooShortMessage(key.Length));
        }

        return key;
    }
}
