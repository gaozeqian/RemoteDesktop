using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace RemoteDesktop.Server.Shared;

public static partial class ServerSetup
{
    /// <summary>
    /// Layers the live settings file over the shipped one, beneath the environment and the command line.
    /// </summary>
    /// <remarks>
    /// Windows only, and only when the file exists: there the program runs from Program Files and would otherwise read
    /// only the shipped defaults. On Linux and macOS the service's working directory (or the package's install
    /// directory) already contains the live file, so adding the same source again would only be a second read of it.
    /// The source is inserted after the shipped <c>appsettings.json</c> on purpose: appending it would put the live
    /// file above the environment and the command line, so <c>--Rendezvous:RelayEndpoint</c> would silently lose.
    /// </remarks>
    public static void AddConfigurationSource(ConfigurationManager configuration, string roleFolder)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleFolder);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(WindowsConfigDirectory, roleFolder, SettingsFileName);
        if (!File.Exists(path))
        {
            return;
        }

        configuration.AddJsonFile(path, optional: true, reloadOnChange: false);
        var sources = configuration.Sources;
        var saved = sources[^1];
        sources.RemoveAt(sources.Count - 1);

        var afterAppSettings = 0;
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is FileConfigurationSource file
                && string.Equals(Path.GetFileName(file.Path), SettingsFileName, StringComparison.OrdinalIgnoreCase))
            {
                afterAppSettings = i + 1;
            }
        }

        sources.Insert(afterAppSettings, saved);
    }

    private static string? DefaultsPath(string? defaultsDirectory, string roleFolder) =>
        defaultsDirectory is null ? null : Path.Combine(defaultsDirectory, roleFolder, SettingsFileName);

    private static JsonObject ReadOrDefault(string livePath, string? defaultsPath)
    {
        if (File.Exists(livePath))
        {
            return Read(livePath);
        }

        return defaultsPath is not null && File.Exists(defaultsPath) ? Read(defaultsPath) : Read(livePath);
    }

    private static JsonObject Read(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            // Comments are allowed: the settings files in the repository carry some, and an operator may add more.
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }) as JsonObject ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{path} is not valid JSON, so it was left alone: {ex.Message}");
        }
    }

    /// <summary>
    /// The private key that belongs to a PEM certificate, if it is beside it.
    /// </summary>
    /// <returns><c>null</c> for a certificate that is not PEM, or whose key is not where it would be expected.</returns>
    private static string? KeyBeside(string certificatePath)
    {
        if (Path.GetExtension(certificatePath) is not (".crt" or ".pem" or ".cer"))
        {
            return null;
        }

        var keyPath = Path.ChangeExtension(certificatePath, ".key");
        return File.Exists(keyPath) ? keyPath : null;
    }

    private static void Write(string path, JsonObject settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Written beside the file and moved into place: an installation that is interrupted must not leave half a
        // settings file behind, because a server that cannot read its settings does not start.
        //
        // The encoder is relaxed so that what is written can be read: the default escapes a "+" as +, and the
        // base64 relay key is full of them. Inside a JSON string that still means the same key, but the notes tell an
        // operator to copy that key to a relay on another machine — and on a command line nothing decodes +, so
        // the relay would be given a different key and refuse every ticket.
        var temporary = path + ".tmp";

        // A leftover from an interrupted run is removed first: opening it would keep whatever mode it was left with,
        // and CreateNew below then applies the owner-only mode at open(2) time instead of after the secrets are in it.
        File.Delete(temporary);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temporary, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(settings.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        }

        // These files carry the relay shared key and, when PostgreSQL is used, its password. The service reads them
        // as its own account; nobody else on the machine has any business doing so. The package's post-install step
        // gives the group the right name (the service account). The file is created owner-only above, so a manual run
        // never has a world-readable key on disk, not even for a moment; the group read the package needs is added
        // here (the create mode does not exist on Windows, hence the check).
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                temporary,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }

        File.Move(temporary, path, overwrite: true);
    }

}
