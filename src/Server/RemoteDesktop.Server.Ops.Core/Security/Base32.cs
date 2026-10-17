using System.Text;

namespace RemoteDesktop.Server.Ops.Core;

/// <summary>
/// RFC 4648 Base32, the alphabet authenticator apps expect. No padding on the way out, padding tolerated on the way in
/// because people paste secrets from places that add it.
/// </summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Upper-case Base32 of <paramref name="data"/>, without padding.</summary>
    public static string Encode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Alphabet[(buffer >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            output.Append(Alphabet[(buffer << (5 - bits)) & 0x1F]);
        }

        return output.ToString();
    }

    /// <summary>The bytes <paramref name="text"/> encodes.</summary>
    /// <exception cref="FormatException">The text contains a character outside the alphabet.</exception>
    public static byte[] Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var trimmed = text.Trim().TrimEnd('=');
        if (trimmed.Length == 0)
        {
            return [];
        }

        var output = new byte[trimmed.Length * 5 / 8];
        var buffer = 0;
        var bits = 0;
        var written = 0;
        foreach (var character in trimmed)
        {
            var value = Alphabet.IndexOf(char.ToUpperInvariant(character), StringComparison.Ordinal);
            if (value < 0)
            {
                throw new FormatException($"'{character}' is not a Base32 character.");
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits < 8)
            {
                continue;
            }

            output[written++] = (byte)((buffer >> (bits - 8)) & 0xFF);
            bits -= 8;
        }

        return written == output.Length ? output : output[..written];
    }
}
