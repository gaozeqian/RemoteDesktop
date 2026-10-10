using System.Diagnostics;

namespace RemoteDesktop.Client.Setup;

/// <summary>
/// Reconfigures the installed host service — the elevated step behind "服务器地址" in the advanced settings.
/// </summary>
/// <remarks>
/// This is the only place where something the user typed is handed to a process running as administrator. The address
/// is parsed first, and what is handed over is the parsed address as a separate argument, never a command line with
/// the text folded into it and never through a shell. An address that is really a fragment of a command line is
/// therefore just a broken address: the receiving executable sees one argument that fails its own validation.
/// </remarks>
internal static class HostServiceSetup
{
    /// <summary>The elevated command that writes the address and restarts the service so it takes effect.</summary>
    /// <param name="executablePath">The installed <c>RemoteDesktop.HostService.exe</c>.</param>
    /// <param name="rendezvous">An address that already went through <c>SystemSetup.TryParseRendezvousUri</c>.</param>
    public static ProcessStartInfo BuildStartInfo(string executablePath, Uri rendezvous)
    {
        ArgumentException.ThrowIfNullOrEmpty(executablePath);
        ArgumentNullException.ThrowIfNull(rendezvous);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        startInfo.ArgumentList.Add("--configure-system");
        startInfo.ArgumentList.Add("--rendezvous");
        startInfo.ArgumentList.Add(rendezvous.AbsoluteUri);
        startInfo.ArgumentList.Add("--restart-service");
        return startInfo;
    }
}
