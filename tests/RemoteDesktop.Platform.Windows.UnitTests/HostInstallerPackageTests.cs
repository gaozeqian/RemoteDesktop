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
        var firewall = Assert.Single(package.Rows("SELECT `Name`, `Protocol`, `Program` FROM `Wix5FirewallException`", 3));
        Assert.Equal("17", firewall[1]); // UDP, what hole punching needs
        Assert.Contains("HostServiceExeFile", firewall[2], StringComparison.Ordinal);
        Assert.Empty(package.Rows("SELECT `Name` FROM `Shortcut`", 1));

        // --- what the installer tells the service ---------------------------------------------------------------
        var properties = package.Rows("SELECT `Property`, `Value` FROM `Property`", 2).ToDictionary(row => row[0], row => row[1]);
        Assert.Contains("RENDEZVOUSURI", properties.Keys);
        Assert.Contains("REMOVEDATA", properties.Keys);
        // Its own product identity: a host package must not upgrade, or be upgraded by, the client package.
        Assert.Equal("Remote Desktop Host", properties["ProductName"]);

        // --- it cannot be installed next to the client package -----------------------------------------------
        // The two products share the RemoteDesktopHost service and the machine's single device identity, so a second
        // installation would be a second service fighting for the same pipe and device code. The package refuses by
        // naming the client's UpgradeCode and a launch condition on it; this checks both halves, because a launch
        // condition whose row never made it into the package is a package that installs and then misbehaves.
        var upgrades = package.Rows("SELECT `UpgradeCode`, `ActionProperty` FROM `Upgrade`", 2);
        Assert.Contains(upgrades, row =>
            row[0].Equals("6f3c1a26-9b57-4d0e-8f2a-5c7b1e4d9a30", StringComparison.OrdinalIgnoreCase));
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
        var actions = package.Rows("SELECT `Action` FROM `CustomAction`", 1).Select(row => row[0]).ToList();
        Assert.Contains("SetFixedPassword", actions);
        Assert.Contains("ClearFixedPassword", actions);
        Assert.Single(package.Rows(
            "SELECT `Action` FROM `InstallExecuteSequence` WHERE `Action` = 'SetFixedPassword'", 1));

        // --- the machine setup actions ---------------------------------------------------------------------------
        foreach (var action in new[] { "ConfigureSystem", "CleanupKeepData", "CleanupRemoveData" })
        {
            Assert.Contains(action, actions);
        }

        var sequence = package.Rows("SELECT `Action`, `Condition` FROM `InstallExecuteSequence`", 2);
        var configure = Assert.Single(sequence, row => row[0] == "ConfigureSystem");
        Assert.Contains("NOT REMOVE", configure[1], StringComparison.Ordinal);
    }
}
