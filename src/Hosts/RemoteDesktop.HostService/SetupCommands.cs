using System.Runtime.Versioning;
using RemoteDesktop.Platform.Windows.Setup;

namespace RemoteDesktop.HostService;

/// <summary>
/// The installer's hooks into the service executable, run instead of the service when the command line asks for them:
/// <c>--configure-system [--rendezvous URI]</c> after the files are copied and <c>--cleanup-system [--keep-data]</c> before
/// they are removed. They run elevated, so they do only what is listed here and report failure through the exit code.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SetupCommands
{
    public const int Success = 0;
    public const int InvalidArguments = 2;

    /// <returns><c>true</c> when the command line was a setup command (then <paramref name="exitCode"/> is the result and the service must not start).</returns>
    public static bool TryRun(string[] args, string installDirectory, string dataDirectory, TextWriter output, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        exitCode = Success;
        if (args.Length == 0 || args[0] is not ("--configure-system" or "--cleanup-system"))
        {
            return false;
        }

        var setup = new SystemSetup(SystemSetupLocations.Machine);
        if (args[0] == "--configure-system")
        {
            exitCode = Configure(args, setup, installDirectory, output);
        }
        else
        {
            Cleanup(args, setup, dataDirectory, output);
        }

        return true;
    }

    private static int Configure(string[] args, SystemSetup setup, string installDirectory, TextWriter output)
    {
        // The server address is the one thing an installation cannot do without; a wrong one fails the install.
        var index = Array.IndexOf(args, "--rendezvous");
        if (index >= 0)
        {
            var uri = index + 1 < args.Length ? args[index + 1] : string.Empty;
            if (uri.Length > 0)
            {
                try
                {
                    SystemSetup.SetRendezvousUri(installDirectory, uri);
                    output.WriteLine($"Rendezvous server set to {uri}.");
                }
                catch (ArgumentException ex)
                {
                    output.WriteLine(ex.Message);
                    return InvalidArguments;
                }
            }
        }

        // Without the policy only Ctrl+Alt+Del from the remote window is lost; that must not fail the installation.
        try
        {
            setup.ConfigureSasPolicy();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            output.WriteLine($"Could not allow Ctrl+Alt+Del from the service: {ex.Message}");
        }

        return Success;
    }

    // Removal always goes on: a leftover setting is better than an uninstall that cannot finish.
    private static void Cleanup(string[] args, SystemSetup setup, string dataDirectory, TextWriter output)
    {
        try
        {
            setup.RevertSasPolicy();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            output.WriteLine($"Could not revert the Ctrl+Alt+Del policy: {ex.Message}");
        }

        if (Array.IndexOf(args, "--keep-data") < 0)
        {
            try
            {
                SystemSetup.RemoveData(dataDirectory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
            {
                output.WriteLine($"Could not remove {dataDirectory}: {ex.Message}");
            }
        }
    }
}
