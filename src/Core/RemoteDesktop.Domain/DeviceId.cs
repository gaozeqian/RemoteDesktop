using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace RemoteDesktop.Domain;

/// <summary>
/// Public identifier of a device, allocated by the rendezvous server. Always 9 decimal digits, first digit non-zero.
/// </summary>
public readonly record struct DeviceId
{
    public const int Length = 9;

    private const int MinValue = 100_000_000;
    private const int MaxValue = 999_999_999;

    public int Value { get; }

    public DeviceId(int value)
    {
        if (value is < MinValue or > MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Device id must be {Length} digits.");
        }

        Value = value;
    }

    /// <summary>Parses user input, tolerating the spaces used for display grouping ("123 456 789").</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out DeviceId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var digits = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (digits.Length != Length || !digits.All(char.IsAsciiDigit) ||
            !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value < MinValue)
        {
            return false;
        }

        id = new DeviceId(value);
        return true;
    }

    public static DeviceId Parse(string text) =>
        TryParse(text, out var id) ? id : throw new FormatException($"'{text}' is not a valid device id.");

    /// <summary>Grouped form for display, e.g. "123 456 789".</summary>
    public string ToDisplayString()
    {
        var s = ToString();
        return $"{s[..3]} {s[3..6]} {s[6..]}";
    }

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
