using Microsoft.Extensions.Configuration;

namespace RemoteDesktop.Application.Hosting;

/// <summary>
/// Inserts one settings file directly after the shipped <c>appsettings.json</c>, beneath the environment and the
/// command line.
/// </summary>
/// <remarks>
/// A settings file belongs to the layer it is installed for: the machine-level one written by the installer, then the
/// person's own one, and neither may quietly outrank a <c>--Transport:RendezvousUri</c> passed for a test. Appending
/// the source would do exactly that, so it is added and then moved; the file name is the same in every layer, which is
/// what makes "after the shipped one" a single rule rather than a list of paths.
/// </remarks>
internal static class SettingsFile
{
    private const string FileName = "appsettings.json";

    public static void AddConfigurationSource(ConfigurationManager configuration, string path)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrEmpty(path);

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
                && string.Equals(Path.GetFileName(file.Path), FileName, StringComparison.OrdinalIgnoreCase))
            {
                afterAppSettings = i + 1;
            }
        }

        sources.Insert(afterAppSettings, saved);
    }
}
