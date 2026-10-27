using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using RemoteDesktop.Domain;

namespace RemoteDesktop.Application.Hosting;

/// <summary>
/// The settings file the person owns: <c>&lt;data directory&gt;/appsettings.json</c>, layered on top of the one the
/// program shipped with.
/// </summary>
/// <remarks>
/// <para>
/// On Windows this file is not used: there the two halves of the product run as different accounts, so the installed
/// service owns the machine-wide file (<see cref="MachineSettings.WindowsPath"/>) and an elevated step
/// (<c>SystemSetup</c>) writes it. Everywhere else both halves run as the same person in the same session, so one
/// file in the data directory drives them both. On macOS the installer cannot write the file next to the program —
/// it sits inside the signed <c>.app</c> bundle, and editing a signed bundle invalidates its signature (and with it
/// the screen-recording and accessibility grants) — so that layer is <see cref="MachineSettings"/> instead, and this
/// file stays the person's override.
/// </para>
/// <para>
/// The data-directory file is what the person can change. The shipped one stays the defaults — under <c>/usr/lib</c>
/// on Linux it belongs to root — which is also why an upgrade can replace it wholesale without touching this one.
/// </para>
/// </remarks>
public static class UserSettings
{
    private const string FileName = "appsettings.json";

    /// <summary>Where the file lives for a data directory.</summary>
    public static string PathFor(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    /// <summary>
    /// Layers the person's file over the shipped one — but underneath the environment and the command line, so
    /// <c>--Transport:RendezvousUri</c> stays an override rather than appearing to be ignored.
    /// </summary>
    /// <remarks>
    /// A missing file adds nothing and installs no watcher: a reloading source costs one inotify instance per host,
    /// and a desktop session only has 128 of them, shared with everything else the user runs.
    /// </remarks>
    public static void AddConfigurationSource(ConfigurationManager configuration, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);

        SettingsFile.AddConfigurationSource(configuration, PathFor(dataDirectory));
    }

    /// <summary>
    /// Sets <c>Transport:RendezvousUri</c> in the person's own settings file, preserving everything else it holds.
    /// </summary>
    /// <remarks>
    /// Only the one key is ever written: everything else keeps coming from the shipped file. A file somebody has
    /// broken by hand is replaced rather than refused — refusing would leave them unable to fix the address from the
    /// interface that exists to fix it.
    /// </remarks>
    public static void SaveRendezvousUri(string dataDirectory, RendezvousAddress address)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);

        var path = PathFor(dataDirectory);
        var root = Read(path);
        var transport = root["Transport"] as JsonObject ?? [];
        transport["RendezvousUri"] = address.AbsoluteUri;
        root["Transport"] = transport;

        Directory.CreateDirectory(dataDirectory);

        // Written beside the file first, so an interrupted write never leaves half a settings file behind.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The settings file, or an empty one. Comments and syntax errors are tolerated: operators edit this by hand.</summary>
    private static JsonObject Read(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(
                File.ReadAllText(path),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
