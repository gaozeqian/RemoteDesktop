using System.Security.Cryptography;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Platform.MacOS;
using RemoteDesktop.Platform.Posix;
using RemoteDesktop.Platform.Windows;

namespace RemoteDesktop.HostService;

/// <summary>
/// Sets or clears the fixed password without starting the service, so an installer (or an operator on a headless
/// machine) can make the host reachable before anybody has a window to type into.
/// </summary>
/// <remarks>
/// The secret goes into the same store the service reads at start-up: DPAPI machine scope on Windows, the login
/// keychain on macOS, and the 0700 file store on Linux. Running this as the wrong account writes a secret the service
/// will never see — on Windows the installer's deferred action runs as SYSTEM, which is the service's own account; on
/// the Unixes it has to be the user whose session the host runs in.
/// </remarks>
internal static class HostCredentialCommands
{
    /// <summary>Runs the command when the arguments ask for it; <c>false</c> means "not this command".</summary>
    public static bool TryRun(string[] args, string dataDirectory, TextWriter output, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || args[0] is not ("--set-fixed-password" or "--clear-fixed-password"))
        {
            return false;
        }

        try
        {
            var store = CreateStore(dataDirectory);

            if (args[0] == "--clear-fixed-password")
            {
                FixedPasswordPolicy.Save(store, null);
                output.WriteLine("The fixed password was cleared. The host will no longer accept it after it restarts.");
                return true;
            }

            var password = args.Length > 1 ? args[1] : null;
            if (string.IsNullOrWhiteSpace(password))
            {
                // The installer's page starts with a space so the edit box is usable; sending that through must not
                // wipe a password somebody configured earlier.
                output.WriteLine("No fixed password was given; the stored one was left unchanged.");
                return true;
            }

            if (!FixedPasswordPolicy.IsAcceptable(password))
            {
                output.WriteLine($"A fixed password must be at least {FixedPasswordPolicy.MinimumLength} characters.");
                exitCode = 2;
                return true;
            }

            FixedPasswordPolicy.Save(store, password);
            output.WriteLine("The fixed password was saved. Restart the host service for it to take effect.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException)
        {
            output.WriteLine($"Could not store the fixed password: {ex.Message}");
            exitCode = 1;
            return true;
        }
    }

    private static ISecretStore CreateStore(string dataDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            // Machine scope: the service runs as LocalSystem and its identity must survive user switches.
            return new DpapiSecretStore(Path.Combine(dataDirectory, "secrets"), DataProtectionScope.LocalMachine);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new KeychainSecretStore(dataDirectory);
        }

        return new FileSecretStore(Path.Combine(dataDirectory, "secrets"));
    }
}
