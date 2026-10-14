using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using RemoteDesktop.Domain;

namespace RemoteDesktop.Platform.Linux.Setup;

/// <summary>
/// The few things the product changes about its own installation on Linux.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <c>SystemSetup</c> on Windows, and much smaller, because almost nothing here needs doing:
/// the controlled end is a <c>systemd --user</c> unit registered by the package rather than a service somebody
/// installs by hand, and there is no policy bit to set.
/// </para>
/// <para>
/// What is left is the one setting a person has to change after installing: which server to talk to.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public static class LinuxSetup
{
    /// <summary>The unit the package registers for the controlled end.</summary>
    public const string HostServiceUnit = "remotedesktop-host";

    /// <summary>
    /// Sets <c>Transport:RendezvousUri</c> in the person's own settings file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The file it writes is the one the programs layer on top of the settings they shipped with. That layering is
    /// the point: the shipped <c>appsettings.json</c> lives beside the program under <c>/usr/lib</c>, where it
    /// belongs to root — so on a machine where nobody is root, the address it carries would be a default that
    /// cannot be changed at all. What is written here is only what is being changed; everything else keeps coming
    /// from the shipped file, which is also why an upgrade can replace that file without touching this one.
    /// </para>
    /// <para>
    /// The controlled end reads the same file. On Windows there are two settings files because the service runs as
    /// another account; here it is the same person in the same session, so one file drives both.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The address is not a <c>ws://</c> or <c>wss://</c> address.</exception>
    public static void SetRendezvousUri(string dataDirectory, string uri)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        if (!RendezvousAddress.TryParse(uri, out var parsed))
        {
            throw new ArgumentException(
                "The rendezvous server must be given as ws://host:port/ws or wss://host/ws.",
                nameof(uri));
        }

        var path = Path.Combine(dataDirectory, "appsettings.json");
        var root = Read(path);
        var transport = root["Transport"] as JsonObject ?? [];
        transport["RendezvousUri"] = parsed.AbsoluteUri;
        root["Transport"] = transport;

        Write(path, root);
    }

    /// <summary>
    /// Restarts the controlled end so it picks up a new address, if this machine is running one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It returns nothing because there is nothing a caller could do differently: the address is already saved to a
    /// file the service reads when it starts, so a machine with no unit to restart — somebody who installed only
    /// the client, or one that is not running it yet — simply takes the change on next start. Reporting a failure
    /// here would be reporting something with no consequence.
    /// </para>
    /// <para>
    /// The client and the controlled end need not even be on the same machine, which is the other reason this is
    /// housekeeping rather than part of saving.
    /// </para>
    /// </remarks>
    public static void RestartHostServiceIfInstalled()
    {
        try
        {
            var startInfo = new ProcessStartInfo("systemctl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            startInfo.ArgumentList.Add("--user");
            startInfo.ArgumentList.Add("restart");
            startInfo.ArgumentList.Add(HostServiceUnit);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return;
            }

            // Drained, or a chatty systemd fills the pipe and the wait below never finishes.
            _ = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit();
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            // No systemctl on this machine, or no systemd to talk to.
        }
    }

    /// <summary>The settings file, or an empty one. Comments are tolerated because operators edit this by hand.</summary>
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
            // A settings file somebody has broken by hand is replaced rather than argued with: the only thing this
            // ever writes is one address, and refusing to save it would leave them with no way to fix it from here.
            return [];
        }
    }

    /// <summary>Written beside the target and moved into place, so an interrupted save leaves no half a file.</summary>
    private static void Write(string path, JsonObject settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}
