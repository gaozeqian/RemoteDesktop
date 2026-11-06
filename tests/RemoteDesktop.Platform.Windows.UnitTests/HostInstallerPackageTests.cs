using System.Globalization;
using System.Runtime.Versioning;

namespace RemoteDesktop.Platform.Windows.UnitTests;

/// <summary>
/// What the host-only MSI has to contain: the service and the agent, and nothing that would turn the machine into a
/// controller as well.
/// </summary>
/// <remarks>
/// The host package is the client package's payload minus the client, built by the same script with -HostOnly. The
/// things worth checking are therefore the two that a mistake would make invisible: that the client really is not in
/// it, and that the service still carries everything that makes it reachable (automatic start, crash recovery, the
/// inbound UDP rule, the machine setup actions). Skipped unless a host installer has been built.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class HostInstallerPackageTests
{
    private static readonly string? Msi = FindInstaller();

    private static string? FindInstaller()
    {
        var given = Environment.GetEnvironmentVariable("RD_INSTALLER_MSI");
        if (!string.IsNullOrEmpty(given))
        {
            // The build script points this at the package it has just produced, whichever role that was.
            return File.Exists(given) ? given : null;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RemoteDesktop.slnx")))
        {
            directory = directory.Parent;
        }

        var artifacts = directory is null ? null : Path.Combine(directory.FullName, "artifacts", "installer", "windows");
        return artifacts is null || !Directory.Exists(artifacts)
            ? null
            : Directory.GetFiles(artifacts, "RemoteDesktop-Host-Windows-*.msi").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    [Fact]
    public void The_package_installs_the_service_and_the_agent_without_the_client()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows Installer is Windows only.");
        Assert.SkipWhen(Msi is null, "No host installer has been built (run build\\installer\\windows\\host\\Build-HostInstaller.ps1).");

        using var package = new MsiPackage(Msi!);

        // --- the payload -----------------------------------------------------------------------------------------
        var files = package.Rows("SELECT `File`, `FileName` FROM `File`", 2);
        Assert.True(files.Count >= 200, $"Only {files.Count} files in the payload.");

        var names = files.Select(row => row[1].Split('|')[^1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var expected in new[] { "RemoteDesktop.HostService.exe", "RemoteDesktop.SessionAgent.exe", "appsettings.json", "msquic.dll" })
        {
            Assert.Contains(expected, names);
        }

        // The point of a package of its own: a controlled machine has no window to drive another one with.
        Assert.DoesNotContain("RemoteDesktop.Client.exe", names);
        var components = package.Rows("SELECT `Component` FROM `Component`", 1).Select(row => row[0]).ToList();
        Assert.DoesNotContain("ClientExe", components);
        Assert.DoesNotContain("StartMenuShortcut", components);

        // --- the service -----------------------------------------------------------------------------------------
        var services = package.Rows("SELECT `Name`, `StartType` FROM `ServiceInstall`", 2);
        var service = Assert.Single(services);
        Assert.Equal("RemoteDesktopHost", service[0]);
        Assert.Equal("2", service[1]); // automatic

        Assert.Single(package.Rows("SELECT `Name` FROM `ServiceControl`", 1));

        // Crash recovery is the one thing a remote-access service cannot be without: nobody is sitting in front of
        // the machine to notice that it is down.
        var restarts = package.Rows(
            "SELECT `ServiceName`, `FirstFailureActionType`, `SecondFailureActionType`, `ThirdFailureActionType`, `RestartServiceDelayInSeconds` FROM `Wix4ServiceConfig`",
            5);
        var host = Assert.Single(restarts);
        Assert.Equal("RemoteDesktopHost", host[0]);
        Assert.Equal("restart", host[1]);
        Assert.Equal("restart", host[2]);
        Assert.Equal("restart", host[3]);
        Assert.Equal("5", host[4]);

        // --- firewall, no shortcut -------------------------------------------------------------------------------
        var firewall = Assert.Single(package.Rows("SELECT `Name`, `Protocol`, `Program`, `InterfaceTypes` FROM `Wix5FirewallException`", 4));
        Assert.Equal("17", firewall[1]); // UDP, what hole punching needs
        Assert.Contains("HostServiceExeFile", firewall[2], StringComparison.Ordinal);
        Assert.Equal("all", firewall[3]); // explicit intent; the extension's "empty Interfaces" log is about NICs, not this
        Assert.Empty(package.Rows("SELECT `Name` FROM `Shortcut`", 1));

        // --- what the installer tells the service ---------------------------------------------------------------
        var properties = package.Rows("SELECT `Property`, `Value` FROM `Property`", 2).ToDictionary(row => row[0], row => row[1]);
        Assert.Contains("RENDEZVOUSURI", properties.Keys);
        Assert.Contains("SERVERCERTIFICATEPATH", properties.Keys);
        Assert.Contains("RENDEZVOUSCERTIFICATEPATH", properties.Keys);
        Assert.Contains("DEFAULT_RENDEZVOUSURI", properties.Keys);
        Assert.Contains("CONNECTIONEDITED", properties.Keys);
        Assert.Contains("REMOVEDATA", properties.Keys);
        Assert.Contains("FIXEDPASSWORDFILE", properties.Keys);
        Assert.Contains("FIXEDPASSWORDFILE", properties["SecureCustomProperties"], StringComparison.Ordinal);
        Assert.Contains("SERVERCERTIFICATEPATH", properties["SecureCustomProperties"], StringComparison.Ordinal);
        Assert.Contains("RENDEZVOUSCERTIFICATEPATH", properties["SecureCustomProperties"], StringComparison.Ordinal);
        // The password itself is hidden from the log; the path the deferred action reads is not a secret.
        Assert.Contains("FIXEDPASSWORD", properties["MsiHiddenProperties"], StringComparison.Ordinal);
        // Its own product identity: a host package must not upgrade, or be upgraded by, the client package.
        Assert.Equal("Remote Desktop Host", properties["ProductName"]);

        // --- it cannot be installed next to the client package -----------------------------------------------
        // The two products share the RemoteDesktopHost service and the machine's single device identity, so a second
        // installation would be a second service fighting for the same pipe and device code. The package refuses by
        // naming the client's UpgradeCode and a launch condition on it; this checks both halves, because a launch
        // condition whose row never made it into the package is a package that installs and then misbehaves.
        var upgrades = package.Rows("SELECT `UpgradeCode`, `ActionProperty` FROM `Upgrade`", 2);
        // Windows Installer stores a GUID in braces; the WiX source spells the same value without them.
        Assert.Contains(upgrades, row =>
            string.Equals(row[0].Trim('{', '}'), "6f3c1a26-9b57-4d0e-8f2a-5c7b1e4d9a30", StringComparison.OrdinalIgnoreCase));
        var launch = package.Rows("SELECT `Condition` FROM `LaunchCondition`", 1);
        Assert.Contains(launch, row => row[0].Contains("CLIENTPACKAGEINSTALLED", StringComparison.Ordinal));

        // --- the unattended password page -----------------------------------------------------------------------
        // The host package has no window after installation, so the wizard is the only way to give a fresh machine a
        // password. Both halves are checked: the page with its password control, and the deferred action that writes
        // the value into the service's machine-scope secret store. A page without the action collects a password and
        // throws it away, which is worse than not asking.
        Assert.Single(package.Rows("SELECT `Dialog` FROM `Dialog` WHERE `Dialog` = 'HostSettingsDlg'", 1));
        Assert.Single(package.Rows(
            "SELECT `Control` FROM `Control` WHERE `Dialog_` = 'HostSettingsDlg' AND `Property` = 'FIXEDPASSWORD'", 1));

        // The host's only installer UI for the server connection: the address and the optional self-signed certificate.
        Assert.Single(package.Rows("SELECT `Dialog` FROM `Dialog` WHERE `Dialog` = 'ConnectionDlg'", 1));
        var connectionControls = package.Rows(
            "SELECT `Control` FROM `Control` WHERE `Dialog_` = 'ConnectionDlg'", 1).Select(row => row[0]).ToList();
        foreach (var control in new[] { "AddressEdit", "CertificateEdit", "CertificateBrowse" })
        {
            Assert.Contains(control, connectionControls);
        }

        // Fresh install / major upgrade -> Connection -> HostSettings. A repair keeps the live values and does not
        // offer the connection page from the password page's Back button.
        var controlEvents = package.Rows(
            "SELECT `Dialog_`, `Control_`, `Event`, `Argument`, `Condition` FROM `ControlEvent`", 5);
        Assert.Contains(controlEvents, row =>
            row[0] == "InstallDirDlg" && row[1] == "Next" && row[3] == "ConnectionDlg"
            && row[4].Contains("NOT Installed", StringComparison.Ordinal));
        Assert.Contains(controlEvents, row =>
            row[0] == "HostSettingsDlg" && row[1] == "Back" && row[3] == "ConnectionDlg"
            && row[4].Contains("NOT Installed", StringComparison.Ordinal));
        Assert.Contains(controlEvents, row =>
            row[0] == "HostSettingsDlg" && row[1] == "Back" && row[3] == "InstallDirDlg"
            && row[4].Contains("Installed", StringComparison.Ordinal)
            && !row[4].Contains("NOT", StringComparison.Ordinal));

        var actionRows = package.Rows("SELECT `Action`, `Type`, `Source`, `Target` FROM `CustomAction`", 4);
        var actions = actionRows.Select(row => row[0]).ToList();
        Assert.Contains("SetFixedPassword", actions);
        Assert.Contains("ClearFixedPassword", actions);
        foreach (var action in new[]
                 {
                     "WriteFixedPasswordFile", "DeleteFixedPasswordFile", "DeleteFixedPasswordFileRollback",
                     "PrefillConnectionSettings", "ValidateConnectionSettings", "BrowseForServerCertificate",
                     "PrepareRendezvousCertificate",
                 })
        {
            Assert.Contains(action, actions);
        }

        // The password goes to a file, not to the host process's command line.
        var setPassword = Assert.Single(actionRows, row => row[0] == "SetFixedPassword");
        Assert.Contains("--set-fixed-password-file", setPassword[3], StringComparison.Ordinal);
        Assert.Contains("[FIXEDPASSWORDFILE]", setPassword[3], StringComparison.Ordinal);
        var setSequence = Assert.Single(package.Rows(
            "SELECT `Action`, `Condition` FROM `InstallExecuteSequence` WHERE `Action` = 'SetFixedPassword'", 2));
        Assert.Contains("FIXEDPASSWORDFILE", setSequence[1], StringComparison.Ordinal);

        // The first upgrade from the old layout copies the address to ProgramData before the old product is removed.
        Assert.Contains("MigrateClientSettings", actions);
        Assert.Single(package.Rows(
            "SELECT `Action` FROM `InstallExecuteSequence` WHERE `Action` = 'MigrateClientSettings'", 1));

        // --- the machine setup actions ---------------------------------------------------------------------------
        foreach (var action in new[] { "ConfigureSystem", "CleanupKeepData", "CleanupRemoveData" })
        {
            Assert.Contains(action, actions);
        }

        var configureAction = Assert.Single(actionRows, row => row[0] == "ConfigureSystem");
        Assert.Contains("--rendezvous", configureAction[3], StringComparison.Ordinal);
        Assert.Contains("--rendezvous-certificate", configureAction[3], StringComparison.Ordinal);
        Assert.Equal(0, int.Parse(configureAction[1], CultureInfo.InvariantCulture) & 64); // failures are visible

        var sequence = package.Rows("SELECT `Action`, `Condition` FROM `InstallExecuteSequence`", 2);
        var configure = Assert.Single(sequence, row => row[0] == "ConfigureSystem");
        Assert.Contains("NOT REMOVE", configure[1], StringComparison.Ordinal);
        Assert.Contains("CONNECTIONEDITED", configure[1], StringComparison.Ordinal);
    }
}
