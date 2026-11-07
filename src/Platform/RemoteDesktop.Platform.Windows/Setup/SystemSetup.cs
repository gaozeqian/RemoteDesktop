using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using RemoteDesktop.Domain;

namespace RemoteDesktop.Platform.Windows.Setup;

/// <summary>Where the setup steps read and write. The defaults are the real machine; tests point them at scratch locations.</summary>
/// <param name="Root">Registry hive or scratch key holding the two sub keys below.</param>
/// <param name="PolicyKey">Where Windows reads <c>SoftwareSASGeneration</c>.</param>
/// <param name="MarkerKey">Where we remember that we are the ones who switched the policy bit on.</param>
[SupportedOSPlatform("windows")]
public sealed record SystemSetupLocations(RegistryKey Root, string PolicyKey, string MarkerKey)
{
    public static SystemSetupLocations Machine => new(
        Registry.LocalMachine,
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System",
        @"SOFTWARE\RemoteDesktop\Installer");
}

/// <summary>
/// The parts of installing and removing the host that are not file copying: allowing the service to send Ctrl+Alt+Del,
/// pointing the apps at the rendezvous server, and removing the machine's data. The installer runs them through
/// <c>RemoteDesktop.HostService.exe --configure-system / --cleanup-system</c>; each is safe to repeat.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SystemSetup(SystemSetupLocations locations)
{
    private const string PolicyValue = "SoftwareSASGeneration";
    private const string MarkerValue = "SasBitAddedByInstaller";
    private const int ServicesMaySendSas = 1;

    /// <summary>
    /// Windows ignores SendSAS from services unless policy bit 0 of <c>SoftwareSASGeneration</c> is set (bit 1 is
    /// another feature's). Only that bit is added, and only if missing; whether it was us is remembered so that
    /// <see cref="RevertSasPolicy"/> never removes a setting somebody else made.
    /// </summary>
    public void ConfigureSasPolicy()
    {
        using var policy = locations.Root.CreateSubKey(locations.PolicyKey, writable: true);
        var current = policy.GetValue(PolicyValue) is int value ? value : 0;
        if ((current & ServicesMaySendSas) != 0)
        {
            return; // already allowed (by policy, or by an earlier install of ours whose marker is kept)
        }

        policy.SetValue(PolicyValue, current | ServicesMaySendSas, RegistryValueKind.DWord);
        using var marker = locations.Root.CreateSubKey(locations.MarkerKey, writable: true);
        marker.SetValue(MarkerValue, 1, RegistryValueKind.DWord);
    }

    /// <summary>Takes back the policy bit if this product added it, and removes its own registry traces.</summary>
    public void RevertSasPolicy()
    {
        using (var marker = locations.Root.OpenSubKey(locations.MarkerKey, writable: false))
        {
            if (marker?.GetValue(MarkerValue) is 1)
            {
                using var policy = locations.Root.OpenSubKey(locations.PolicyKey, writable: true);
                if (policy?.GetValue(PolicyValue) is int current)
                {
                    var remaining = current & ~ServicesMaySendSas;
                    if (remaining == 0)
                    {
                        policy.DeleteValue(PolicyValue, throwOnMissingValue: false);
                    }
                    else
                    {
                        policy.SetValue(PolicyValue, remaining, RegistryValueKind.DWord);
                    }
                }
            }
        }

        locations.Root.DeleteSubKeyTree(locations.MarkerKey, throwOnMissingSubKey: false);

        // "SOFTWARE\RemoteDesktop" itself goes too, when nothing else lives in it.
        var parent = locations.MarkerKey[..Math.Max(0, locations.MarkerKey.LastIndexOf('\\'))];
        if (parent.Length > 0)
        {
            using var key = locations.Root.OpenSubKey(parent);
            if (key is { SubKeyCount: 0, ValueCount: 0 })
            {
                locations.Root.DeleteSubKey(parent, throwOnMissingSubKey: false);
            }
        }
    }

    /// <summary>
    /// Parses the one address shape this product supports: <c>ws://host[:port][/path]</c>, or the same wrapped in TLS
    /// as <c>wss://</c>. The elevated setup step and the client both come through here, so an address the client lets
    /// through is never one the service refuses, and the two can not drift apart.
    /// </summary>
    /// <remarks>
    /// The grammar itself lives in <see cref="RendezvousAddress"/>, shared because every end of the product has to
    /// agree on what an address is: the installer hands one to the service, the client validates whatever somebody
    /// typed into the box, and macOS has no service at all. This method stays as the Windows-facing name for it.
    /// </remarks>
    public static bool TryParseRendezvousUri(string? address, out Uri parsed)
    {
        if (RendezvousAddress.TryParse(address, out var rendezvous))
        {
            parsed = rendezvous.Value;
            return true;
        }

        parsed = null!;
        return false;
    }

    /// <summary>Sets <c>Transport:RendezvousUri</c> in the machine-wide appsettings.json under a data directory.</summary>
    /// <remarks>
    /// The directory is <c>%ProgramData%\RemoteDesktop</c> on an installed machine (see
    /// <c>RemoteDesktop.Application.Hosting.DataDirectory.ForMachine</c>), not the program folder: the MSI's major
    /// upgrade removes the old product's files from Program Files, so a live settings file there would be deleted on
    /// every upgrade.
    /// </remarks>
    /// <exception cref="ArgumentException">The address is not a supported URI, or uses non-loopback plain-text signaling.</exception>
    public static void SetRendezvousUri(string settingsDirectory, string uri)
    {
        ArgumentException.ThrowIfNullOrEmpty(settingsDirectory);
        if (!TryParseRendezvousUri(uri, out var parsed))
        {
            throw new ArgumentException("The rendezvous server must be given as ws://host:port/ws or wss://host/ws.", nameof(uri));
        }

        if (parsed.Scheme == "ws" && !parsed.IsLoopback)
        {
            throw new ArgumentException("A non-loopback rendezvous server must use wss://; plain ws:// is only supported for loopback addresses.", nameof(uri));
        }

        UpdateSettings(settingsDirectory, transport => transport["RendezvousUri"] = parsed.AbsoluteUri);
    }

    /// <summary>
    /// Sets or removes <c>Transport:RendezvousCertificatePath</c>: the public certificate the connection pins for a
    /// <c>wss://</c> server. An empty path removes the pin again, which is what "the certificate is from a public
    /// authority" and "the address is ws://" both want.
    /// </summary>
    /// <remarks>
    /// The installer copies the public half into <c>%ProgramData%\RemoteDesktop\certificates</c> first and passes that
    /// file here; this method only writes the setting, so it does not need to be told where the operator's original
    /// file was. The path has to be absolute and exist: a pin the application cannot load is a start-up failure for
    /// the service, which is worse than refusing the installation now.
    /// </remarks>
    /// <exception cref="ArgumentException">The certificate path is not absolute or does not exist.</exception>
    public static void SetRendezvousCertificate(string settingsDirectory, string? certificatePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(settingsDirectory);
        certificatePath = certificatePath?.Trim();
        if (!string.IsNullOrEmpty(certificatePath))
        {
            if (!Path.IsPathRooted(certificatePath))
            {
                throw new ArgumentException("The server certificate must be given as an absolute path.", nameof(certificatePath));
            }

            if (!File.Exists(certificatePath))
            {
                throw new ArgumentException($"The server certificate '{certificatePath}' does not exist.", nameof(certificatePath));
            }
        }

        UpdateSettings(settingsDirectory, transport =>
        {
            if (string.IsNullOrEmpty(certificatePath))
            {
                transport.Remove("RendezvousCertificatePath");
            }
            else
            {
                transport["RendezvousCertificatePath"] = certificatePath;
            }
        });
    }

    private static void UpdateSettings(string settingsDirectory, Action<JsonObject> update)
    {
        Directory.CreateDirectory(settingsDirectory);
        var path = Path.Combine(settingsDirectory, "appsettings.json");
        var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }) as JsonObject ?? [] : [];
        var transport = root["Transport"] as JsonObject ?? [];
        update(transport);
        root["Transport"] = transport;

        // Written next to the file first, so an interrupted install never leaves half a settings file behind.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Deletes the machine's data folder (device identity, logs). Refuses any folder that is not named RemoteDesktop,
    /// so a wrong path on a command line can never wipe something else.
    /// </summary>
    /// <returns><c>true</c> when a folder was removed.</returns>
    public static bool RemoveData(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        var full = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetFileName(full), "RemoteDesktop", StringComparison.OrdinalIgnoreCase) || Path.GetPathRoot(full) == full)
        {
            throw new ArgumentException("Not a RemoteDesktop data folder.", nameof(dataDirectory));
        }

        if (!Directory.Exists(full))
        {
            return false;
        }

        Directory.Delete(full, recursive: true);
        return true;
    }
}
