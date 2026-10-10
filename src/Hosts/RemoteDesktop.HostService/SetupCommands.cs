using System.ComponentModel;
using System.Runtime.Versioning;
using System.ServiceProcess;
using RemoteDesktop.Platform.Windows.Setup;

namespace RemoteDesktop.HostService;

/// <summary>
/// The installer's hooks into the service executable, run instead of the service when the command line asks for them:
/// <c>--configure-system [--rendezvous URI] [--restart-service]</c> after the files are copied and
/// <c>--cleanup-system [--keep-data]</c> before they are removed. They run elevated, so they do only what is listed
/// here and report failure through the exit code.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SetupCommands
{
    public const int Success = 0;
    public const int InvalidArguments = 2;

    /// <summary>The name the service is installed under; the installer registers it and the client restarts it.</summary>
    public const string ServiceName = "RemoteDesktopHost";

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
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    // The installer runs this as the system and does not care about the exit code, but somebody running
                    // it by hand should get a sentence rather than a stack trace.
                    output.WriteLine($"Could not write the server address into {installDirectory}: {ex.Message}");
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

        // Asked for by the client after the address changed: a running service reads its settings once, at start-up.
        if (Array.IndexOf(args, "--restart-service") >= 0)
        {
            RestartService(output);
        }

        return Success;
    }

    /// <summary>
    /// Restarts the installed service so it picks up the settings that were just written. Failing here does not fail
    /// the step: the address is already on disk and the service uses it the next time it starts.
    /// </summary>
    private static void RestartService(TextWriter output)
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            if (service.Status is not ServiceControllerStatus.Stopped)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }

            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            output.WriteLine($"{ServiceName} restarted.");
        }
        // WaitForStatus has a TimeoutException of its own in this namespace; the one it throws is that one.
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or System.ServiceProcess.TimeoutException or UnauthorizedAccessException)
        {
            output.WriteLine($"Could not restart {ServiceName}: {ex.Message}");
        }
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
