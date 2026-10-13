using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemoteDesktop.Client.Storage;

/// <summary>
/// The signaling address somebody typed into 高级设置, kept in the data directory.
/// </summary>
/// <remarks>
/// Deliberately not written into the <c>appsettings.json</c> next to the executable: on macOS that file sits inside the
/// .app bundle, which is code-signed, and editing a signed bundle invalidates its signature. There that is not a
/// cosmetic problem — macOS keys the screen-recording and accessibility grants to the signing identity, so a rewritten
/// bundle loses its permissions and the user has to grant them again. The data directory is per-user and outside the
/// bundle, which is where a setting the user chose belongs anyway.
/// <para>
/// The file has the same shape as <c>appsettings.json</c> on purpose: <see cref="ClientHost"/> loads it as an ordinary
/// configuration source, so nothing downstream has to know that this one setting has a second home.
/// </para>
/// </remarks>
internal static class ServerAddressStore
{
    private const string FileName = "settings.json";

    /// <summary>Where the file lives for a data directory. Exposed so the host can add it as a configuration source.</summary>
    public static string PathFor(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    /// <summary>Writes the address, preserving anything else the file already holds.</summary>
    public static void Save(string dataDirectory, Uri address)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        ArgumentNullException.ThrowIfNull(address);

        var path = PathFor(dataDirectory);
        var root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }) as JsonObject ?? []
            : [];
        var transport = root["Transport"] as JsonObject ?? [];
        transport["RendezvousUri"] = address.AbsoluteUri;
        root["Transport"] = transport;

        Directory.CreateDirectory(dataDirectory);

        // Written beside the file first, so an interrupted write never leaves half a settings file behind.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}
