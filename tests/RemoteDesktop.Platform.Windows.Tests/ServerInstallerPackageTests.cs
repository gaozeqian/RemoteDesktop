using System.Globalization;
using System.Runtime.Versioning;

namespace RemoteDesktop.Platform.Windows.Tests;

/// <summary>
/// What has to be in the server installer for it to be worth anything: two services that start by themselves, the
/// three ports the servers need opened, both settings files in place, and the one step that ties them together —
/// the key that signs relay tickets, which both servers have to agree on.
/// </summary>
/// <remarks>
/// Checked by opening the package rather than by installing it. WiX 5 drops things without a word (a fragment with
/// no component, a preprocessor variable that is not defined for the component that uses it), and what comes out
/// still builds and still installs — just without the part that was dropped. This test exists because that happened.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ServerInstallerPackageTests
{
    private static readonly string? Msi = FindInstaller();

    private static string? FindInstaller()
    {
        var given = Environment.GetEnvironmentVariable("RD_SERVER_INSTALLER_MSI");
        if (!string.IsNullOrEmpty(given))
        {
            return File.Exists(given) ? given : null;
        }

        // The repository root is the folder with the solution file; the installer ends up in its own artifacts folder.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RemoteDesktop.slnx")))
        {
            directory = directory.Parent;
        }

        var artifacts = directory is null ? null : Path.Combine(directory.FullName, "build", "installer", "server", "windows", "artifacts");
        return artifacts is null || !Directory.Exists(artifacts)
            ? null
            : Directory.GetFiles(artifacts, "*.msi").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    [Fact]
    public void The_package_installs_both_servers_the_ports_they_need_and_the_settings_they_share()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows Installer is Windows only.");
        Assert.SkipWhen(Msi is null, "No server installer has been built (run build\\installer\\server\\build-server-installer.ps1).");

        using var package = new MsiPackage(Msi!);

        // --- the two programs, each with the runtime that comes with it -------------------------------------------
        var files = package.Rows("SELECT `File`, `FileName` FROM `File`", 2);
        var names = files.Select(row => row[1].Split('|')[^1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var expected in new[] { "RemoteDesktop.Server.Rendezvous.exe", "RemoteDesktop.Server.Relay.exe", "appsettings.json", "configure-server.ps1", "如何配置.txt" })
        {
            Assert.Contains(expected, names);
        }

        Assert.True(files.Count >= 200, $"Only {files.Count} files in the payload.");

        // --- two services that start on their own, as the system -----------------------------------------------
        var services = package.Rows("SELECT `Name`, `StartType` FROM `ServiceInstall`", 2);
        var installed = services.Select(row => row[0]).ToList();
        foreach (var service in new[] { "RemoteDesktopRendezvous", "RemoteDesktopRelay" })
        {
            Assert.True(installed.Contains(service), $"The package does not install the {service} service.");
        }

        Assert.All(services, row => Assert.Equal("2", row[1])); // 2 = automatic

        // Stopped on uninstall and on upgrades, or the executable is in use while it is being replaced.
        var controls = package.Rows("SELECT `Name` FROM `ServiceControl`", 1).Select(row => row[0]).ToList();
        foreach (var service in new[] { "RemoteDesktopRendezvous", "RemoteDesktopRelay" })
        {
            Assert.True(controls.Contains(service), $"The package never stops the {service} service.");
        }

        // --- the three ports the servers need -------------------------------------------------------------------
        // The column is `Program` and not `Program_`: the underscore belongs to the built-in MsiFirewall table, and
        // asking for the wrong name makes the query fail in a way this reader turns into "no rows at all".
        var firewall = package.Rows("SELECT `Name`, `Protocol`, `Port`, `Program` FROM `Wix5FirewallException`", 4);
        Assert.Equal(3, firewall.Count);

        // Each rule is on a program and not on a port alone: "anything may listen on 5080" is a different and much
        // wider statement than "this server may".
        Assert.All(firewall, rule => Assert.Contains("ExeFile", rule[3], StringComparison.Ordinal));

        // The protocol is the number the rule is written with (6 = TCP, 17 = UDP), not the word.
        var rules = firewall.Select(row => (Protocol: Protocol(row[1]), Port: row[2])).ToList();
        Assert.Contains(rules, rule => rule == ("tcp", "5080"));
        Assert.Contains(rules, rule => rule == ("udp", "5081"));
        Assert.Contains(rules, rule => rule == ("udp", "5082"));

        static string Protocol(string value) => value switch
        {
            "6" => "tcp",
            "17" => "udp",
            _ => value,
        };

        // --- what an operator can set while installing ----------------------------------------------------------
        var properties = package.Rows("SELECT `Property`, `Value` FROM `Property`", 2).ToDictionary(row => row[0], row => row[1]);
        foreach (var name in new[] { "RENDEZVOUSURL", "RELAYPUBLICENDPOINT", "RELAYSHAREDKEY", "POSTGRESCONNECTIONSTRING", "TLSCERTIFICATEPATH", "NOFIREWALL" })
        {
            // A property with no value never reaches the property table, and the custom action that reads it finds
            // nothing — so each of them carries one, even when all it says is "decide for me".
            Assert.True(properties.ContainsKey(name), $"The package has no {name} property.");
            Assert.True(properties.ContainsKey(name), $"The {name} property is not in the property table at all, which is what disables a control that shows it.");
        }

        Assert.Equal("auto", properties["RELAYSHAREDKEY"]);
        Assert.Contains("5080", properties["RENDEZVOUSURL"], StringComparison.Ordinal);

        // --- the page that makes this package answerable without a command line --------------------------------
        // Without it a double-click install takes every default, and the defaults are "no database" and "plain http":
        // the two settings whose consequences are worst, and whose warnings were only ever written to a log file.
        var dialogs = package.Rows("SELECT `Dialog` FROM `Dialog`", 1).Select(row => row[0]).ToList();
        Assert.Contains("ServerSettingsDlg", dialogs);

        // The wizard has to reach it. Exactly one NewDialog on InstallDirDlg's Next: two would mean the wizard's own
        // publish is still there alongside ours, and which of them wins is not something to leave to chance.
        var events = package.Rows("SELECT `Control_`, `Event`, `Argument`, `Ordering`, `Condition` FROM `ControlEvent` WHERE `Dialog_` = 'InstallDirDlg'", 5);
        var forward = events.Where(row => row[0] == "Next" && row[1] == "NewDialog").ToList();

        // The dialog set keeps its own transition to the confirmation page, so there are two of these. What matters is
        // which one takes effect: control events run in ordering order and the last matching transition is the one the
        // wizard follows, so ours has to be the last — not merely present.
        var last = forward.OrderByDescending(row => int.Parse(row[3], CultureInfo.InvariantCulture)).First();
        Assert.Equal("ServerSettingsDlg", last[2]);
        Assert.True(int.Parse(last[3], CultureInfo.InvariantCulture) > forward.Count, "our transition has to come after the dialog set's own");

        // And back returns to it from the confirmation, so the page is not a one-way trip.
        var back = package.Rows("SELECT `Control_`, `Event`, `Argument` FROM `ControlEvent` WHERE `Dialog_` = 'VerifyReadyDlg'", 3);
        Assert.Contains(back, row => row[0] == "Back" && row[1] == "NewDialog" && row[2] == "ServerSettingsDlg");

        // Every control on the page writes a property the deferred action already reads, which is what makes the page
        // work without touching the install logic.
        var pageControls = package.Rows("SELECT `Control`, `Property` FROM `Control` WHERE `Dialog_` = 'ServerSettingsDlg'", 2);
        foreach (var property in new[] { "POSTGRESCONNECTIONSTRING", "RELAYPUBLICENDPOINT", "TLSCERTIFICATEPATH", "TLSCERTIFICATEPASSWORD" })
        {
            Assert.True(pageControls.Any(row => row[1] == property), $"The settings page has no control for {property}.");
        }

        Assert.True(pageControls.Any(row => row[1] == "TLSCHOICE"), "The settings page has no TLS choice.");

        // Nothing on that page may sit outside it. The coordinates were chosen by hand, and a control that falls off the
        // page is invisible rather than broken — the kind of mistake nobody notices until a reader asks where the
        // certificate path went.
        var bounds = package.Rows("SELECT `Width`, `Height` FROM `Dialog` WHERE `Dialog` = 'ServerSettingsDlg'", 2);
        var (pageWidth, pageHeight) = (int.Parse(bounds[0][0], CultureInfo.InvariantCulture), int.Parse(bounds[0][1], CultureInfo.InvariantCulture));
        foreach (var control in pageControls)
        {
            var position = package.Rows($"SELECT `X`, `Y`, `Width`, `Height` FROM `Control` WHERE `Dialog_` = 'ServerSettingsDlg' AND `Control` = '{control[0]}'", 4);
            if (position.Count == 0 || position[0].Any(value => !int.TryParse(value, out _)))
            {
                continue; // the dialog's own bookkeeping rows (First, Default, Cancel) have no coordinates
            }

            var (x, y, width, height) = (int.Parse(position[0][0], CultureInfo.InvariantCulture), int.Parse(position[0][1], CultureInfo.InvariantCulture), int.Parse(position[0][2], CultureInfo.InvariantCulture), int.Parse(position[0][3], CultureInfo.InvariantCulture));
            Assert.True(x + width <= pageWidth && y + height <= pageHeight, $"{control[0]} sits outside the page: {x},{y} {width}x{height} in {pageWidth}x{pageHeight}.");
        }

        // The start menu entry, pointed at the page in the installation folder rather than at a URL: the repository is
        // private, so a link to it would not help whoever runs the server.
        var shortcuts = package.Rows("SELECT `Name`, `Target` FROM `Shortcut`", 2);
        var howTo = Assert.Single(shortcuts, row => row[0] == "服务器配置说明");
        Assert.Contains("如何配置.txt", howTo[1], StringComparison.Ordinal);

        // --- the step that gives both servers the same key ------------------------------------------------------
        var actions = package.Rows("SELECT `Action` FROM `CustomAction`", 1).Select(row => row[0]).ToList();
        Assert.Contains(actions, action => action.Contains("Configure", StringComparison.OrdinalIgnoreCase));

        var sequence = package.Rows("SELECT `Action`, `Condition` FROM `InstallExecuteSequence`", 2);
        var configure = sequence.Where(row => row[0].Contains("Configure", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(configure);
        Assert.All(configure, row => Assert.Contains("NOT REMOVE", row[1], StringComparison.Ordinal));
    }
}
