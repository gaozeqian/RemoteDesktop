using System.Text;
using System.Text.Json;

namespace RemoteDesktop.Application.Hosting;

public sealed record HostControlRequest(string Command, string? Argument = null);

/// <summary>
/// Wire format of the UI ↔ service pipe: one JSON object per line, request then response. Commands are
/// <see cref="StatusCommand"/> (answered with a <see cref="HostStatus"/>), <see cref="RotateCommand"/>, and
/// <see cref="DowngradeCommand"/> / <see cref="DisconnectCommand"/> / <see cref="CameraStopCommand"/>, which name a session in the argument,
/// and <see cref="CameraAllowCommand"/> (argument <c>1</c> or <c>0</c>).
/// </summary>
public static class HostControlProtocol
{
    public const string PipeName = "RemoteDesktop.HostControl";
    public const string StatusCommand = "status";
    public const string RotateCommand = "rotate";
    public const string DowngradeCommand = "downgrade";
    public const string DisconnectCommand = "disconnect";
    public const string CameraAllowCommand = "camera-allow";
    public const string CameraStopCommand = "camera-stop";
    public const string FixedPasswordCommand = "fixed-password";

    /// <summary>Requests and responses are a few hundred bytes; anything bigger is not from us.</summary>
    public const int MaxLineBytes = 16 * 1024;

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    public static async Task WriteLineAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var line = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads up to the next newline; <c>null</c> when the peer closed first.</summary>
    /// <exception cref="InvalidDataException">The line exceeds <see cref="MaxLineBytes"/>.</exception>
    public static async Task<T?> ReadLineAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var bytes = new List<byte>(256);
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
            {
                return default;
            }

            if (one[0] == (byte)'\n')
            {
                break;
            }

            if (bytes.Count >= MaxLineBytes)
            {
                throw new InvalidDataException("Host control message too long.");
            }

            bytes.Add(one[0]);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString([.. bytes]), Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Malformed host control message.", ex);
        }
    }
}
