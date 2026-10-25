using System.Text;
using System.Text.Json;

namespace RemoteDesktop.Application.Hosting;

public sealed record HostControlRequest(string Command, string? Argument = null);

/// <summary>
/// Wire format of the UI ↔ service pipe: one JSON object per line, request then response. Commands are
/// <see cref="StatusCommand"/> (answered with a <see cref="HostStatus"/>), <see cref="RotateCommand"/>,
/// <see cref="FixedPasswordCommand"/>, and <see cref="DowngradeCommand"/> / <see cref="DisconnectCommand"/> /
/// <see cref="CameraStopCommand"/>, which name a session in the argument.
/// </summary>
public static class HostControlProtocol
{
    public const string PipeName = "RemoteDesktop.HostControl";

    /// <summary>
    /// The unit the Linux host package installs. A live control socket without this file is a process whose package
    /// has been removed; treating it as the background service leaves the client delegating to a controlled end the
    /// person can no longer see or remove from the package manager.
    /// </summary>
    public const string LinuxHostServiceUnit = "/usr/lib/systemd/user/app-RemoteDesktopHost.service";

    /// <summary>
    /// The macOS host package's application and login item. The client uses them for the same question the Linux
    /// unit answers: is a background controlled end installed, or is this a socket left by a removed package?
    /// </summary>
    /// <remarks>
    /// The host lives under Library/Application Support, not /Applications: it has no window, and an app-looking
    /// bundle beside Remote Desktop.app invited people to double-click it and wonder why nothing happened. The legacy
    /// path is checked as well so a client running against an older host package still finds its service.
    /// </remarks>
    public const string MacOSHostApp =
        "/Library/Application Support/RemoteDesktop/Host/Remote Desktop Host.app";
    public const string MacOSHostLegacyApp = "/Applications/Remote Desktop Host.app";
    public const string MacOSHostAgentPlist = "/Library/LaunchAgents/com.remotedesktop.host.plist";

    /// <summary>Whether any form of the macOS host package is installed.</summary>
    public static bool MacOSHostInstalled =>
        File.Exists(MacOSHostApp) || File.Exists(MacOSHostLegacyApp) || File.Exists(MacOSHostAgentPlist);

    public const string StatusCommand = "status";
    public const string RotateCommand = "rotate";
    public const string DowngradeCommand = "downgrade";
    public const string DisconnectCommand = "disconnect";
    public const string CameraStopCommand = "camera-stop";
    public const string FixedPasswordCommand = "fixed-password";

    /// <summary>Grants or refuses the host's camera use; the argument is <c>allow</c> or <c>deny</c>.</summary>
    public const string CameraPermissionCommand = "camera-permission";

    public const string AllowCamera = "allow";

    public const string DenyCamera = "deny";

    /// <summary>The service refused a command; the argument carries the reason.</summary>
    public const string ErrorCommand = "error";

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
