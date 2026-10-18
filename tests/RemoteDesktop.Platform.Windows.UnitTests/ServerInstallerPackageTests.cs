using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using WixToolset.Dtf.WindowsInstaller;

namespace RemoteDesktop.Platform.Windows.UnitTests;

/// <summary>
/// What has to be in the server installer for it to be worth anything: three services that start by themselves, the
/// three ports the public servers need opened, their settings files in place, and the one step that ties them together
/// — the key that signs relay tickets, which both network servers have to agree on.
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

        // The repository root is the folder with the solution file; installers land under its single artifacts/ root
        // (see CONTRIBUTING.md, "构建产物布局"). The server shares that directory with the client, so the file name is
        // what tells them apart — hence the prefix rather than a bare "*.msi".
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RemoteDesktop.slnx")))
        {
            directory = directory.Parent;
        }

        var artifacts = directory is null ? null : Path.Combine(directory.FullName, "artifacts", "installer", "windows");
        return artifacts is null || !Directory.Exists(artifacts)
            ? null
            : Directory.GetFiles(artifacts, "RemoteDesktop.Server-*.msi").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    [Fact]
    public void The_package_installs_both_servers_the_ports_they_need_and_the_settings_they_share()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows Installer is Windows only.");
        Assert.SkipWhen(Msi is null, "No server installer has been built (run build\\installer\\server\\windows\\Build-ServerInstaller.ps1).");

        using var package = new MsiPackage(Msi!);

        // --- the three programs, each with the runtime that comes with it ----------------------------------------
        var files = package.Rows("SELECT `File`, `FileName` FROM `File`", 2);
        var names = files.Select(row => row[1].Split('|')[^1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var expected in new[] { "RemoteDesktop.Server.Rendezvous.exe", "RemoteDesktop.Server.Relay.exe", "RemoteDesktop.Server.Ops.exe", "appsettings.json", "Set-ServerConfiguration.ps1", "how-to-configure.txt" })
        {
            Assert.Contains(expected, names);
        }

        Assert.True(files.Count >= 300, $"Only {files.Count} files in the payload.");

        // --- three services that start on their own, as the system ----------------------------------------------
        var services = package.Rows("SELECT `Name`, `StartType` FROM `ServiceInstall`", 2);
        var installed = services.Select(row => row[0]).ToList();
        foreach (var service in new[] { "RemoteDesktopRendezvous", "RemoteDesktopRelay", "RemoteDesktopOps" })
        {
            Assert.True(installed.Contains(service), $"The package does not install the {service} service.");
        }

        Assert.All(services, row => Assert.Equal("2", row[1])); // 2 = automatic

        // Stopped on uninstall and on upgrades, or the executable is in use while it is being replaced.
        var controls = package.Rows("SELECT `Name` FROM `ServiceControl`", 1).Select(row => row[0]).ToList();
        foreach (var service in new[] { "RemoteDesktopRendezvous", "RemoteDesktopRelay", "RemoteDesktopOps" })
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

        // And the two addresses the page shows are filled in immediately before that transition. They cannot be filled
        // in by the page itself: a control reads its property once, when the dialog is built, so a value set after that
        // is one nobody sees.
        var intoPage = package.Rows("SELECT `Event`, `Argument`, `Ordering` FROM `ControlEvent` WHERE `Dialog_` = 'InstallDirDlg' AND `Control_` = 'Next'", 3);
        var fill = intoPage.Where(row => row[0] == "DoAction" && row[1] == "PrefillSettings").ToList();
        Assert.Single(fill);
        Assert.True(int.Parse(fill[0][2], CultureInfo.InvariantCulture) < int.Parse(last[3], CultureInfo.InvariantCulture),
            "the addresses have to be filled in before the page that shows them is entered");

        // And back returns to it from the confirmation, so the page is not a one-way trip.
        var back = package.Rows("SELECT `Control_`, `Event`, `Argument` FROM `ControlEvent` WHERE `Dialog_` = 'VerifyReadyDlg'", 3);
        Assert.Contains(back, row => row[0] == "Back" && row[1] == "NewDialog" && row[2] == "ServerSettingsDlg");

        // No control event may be called SetProperty. It reads like the way to set a property from a dialog and is not:
        // a property is set by naming it in the event column in brackets, and any other unrecognised name makes the
        // installer report "Error 2812: The event X is not recognized" and abandon the wizard. That is how this one
        // behaved — and because both of the events using it carried a condition, the failure only appeared when the
        // page was used with one of those two choices made, which is why it survived every earlier walk through.
        var everyEvent = package.Rows("SELECT `Dialog_`, `Control_`, `Event` FROM `ControlEvent`", 3);
        Assert.DoesNotContain(everyEvent, row => row[2] == "SetProperty");

        // And the mapping itself, since the radio group's property and the file box's property are neither of them the
        // one the install acts on.
        var mapping = package.Rows("SELECT `Event`, `Argument`, `Condition` FROM `ControlEvent` WHERE `Dialog_` = 'ServerSettingsDlg' AND `Control_` = 'Next'", 3);
        Assert.Contains(mapping, row => row[0] == "[TLSCERTIFICATEPATH]" && row[1] == "auto" && row[2].Contains("TLSCHOICE", StringComparison.Ordinal));
        Assert.Contains(mapping, row => row[0] == "[TLSCERTIFICATEPATH]" && row[1] == "generate" && row[2].Contains("TLSCHOICE", StringComparison.Ordinal));
        Assert.Contains(mapping, row => row[0] == "[TLSCERTIFICATEPATH]" && row[1] == "[TLSCERTIFICATEFILE]" && row[2].Contains("TLSCHOICE", StringComparison.Ordinal));

        // Tidying runs on the way out, before any of that. It is what makes the space these boxes start with harmless:
        // without it, a password typed after that space would be sent on with the space in front of it and the
        // certificate would not open.
        var nextEvents = package.Rows("SELECT `Event`, `Argument`, `Ordering` FROM `ControlEvent` WHERE `Dialog_` = 'ServerSettingsDlg' AND `Control_` = 'Next'", 3);
        var tidy = nextEvents.Where(row => row[0] == "DoAction" && row[1] == "NormalizeSettings").ToList();
        Assert.Single(tidy);
        var tidyOrder = int.Parse(tidy[0][2], CultureInfo.InvariantCulture);
        foreach (var row in nextEvents.Where(row => row[0] == "[TLSCERTIFICATEPATH]"))
        {
            Assert.True(tidyOrder < int.Parse(row[2], CultureInfo.InvariantCulture), "tidying has to happen before the value is mapped onward");
        }

        // Every control on the page writes a property the deferred action already reads, which is what makes the page
        // work without touching the install logic.
        var pageControls = package.Rows("SELECT `Control`, `Property` FROM `Control` WHERE `Dialog_` = 'ServerSettingsDlg'", 2);
        foreach (var property in new[] { "POSTGRESCONNECTIONSTRING", "RELAYPUBLICENDPOINT", "TLSCERTIFICATEFILE", "TLSCERTIFICATEPASSWORD" })
        {
            Assert.True(pageControls.Any(row => row[1] == property), $"The settings page has no control for {property}.");
        }

        // The certificate box writes its own property, never the one the install reads: that one holds a word rather
        // than a path whenever TLS is off or the certificate is generated, and the box used to show it — choosing
        // "generate" and pressing Next left the word sitting in a field labelled "path".
        Assert.DoesNotContain(pageControls, row => row[1] == "TLSCERTIFICATEPATH");

        // And the box that can be left empty arrives with a space in it rather than nothing. A property with no value
        // never reaches the property table, and a control bound to a missing property comes out greyed and untypable —
        // the state the certificate path box was in before it was given a value.
        Assert.Equal(" ", properties["TLSCERTIFICATEFILE"]);

        Assert.True(pageControls.Any(row => row[1] == "TLSCHOICE"), "The settings page has no TLS choice.");

        // Picking a certificate file has to end by navigating to another dialog, and must not end with a Reset. Reset
        // reads like "make the control show the property again" and is the opposite: it restores the control's original
        // text and writes that into the property, which silently discarded the chosen path — the installer's own log
        // showed the path being set and then replaced by "auto" twenty-seven milliseconds later. It has to be a
        // transition rather than SpawnDialog (the same log showed the page below a spawned dialog never being built
        // again when it closed), and the target cannot be this page itself: that was tried, and the installer refuses
        // with "Error 2856: Creating a second copy of the dialog ServerSettingsDlg" and stops the wizard.
        var browseEvents = package.Rows("SELECT `Event`, `Argument`, `Condition` FROM `ControlEvent` WHERE `Dialog_` = 'ServerSettingsDlg' AND `Control_` = 'TlsBrowseButton'", 3);
        Assert.Contains(browseEvents, row => row[0] == "DoAction" && row[1] == "BrowseForCertificate");
        Assert.Contains(browseEvents, row => row[0] == "NewDialog" && row[1] == "CertificateChosenDlg" && row[2].Contains("TLSCERTIFICATECHOSEN", StringComparison.Ordinal));
        Assert.DoesNotContain(browseEvents, row => row[0] == "Reset");
        Assert.DoesNotContain(browseEvents, row => row[0] == "SpawnDialog");
        Assert.DoesNotContain(browseEvents, row => row[1] == "ServerSettingsDlg");

        // The dialog that confirms the choice, and shows it: the page behind cannot while it is on screen. Its one
        // button has to lead back, or the choice would be shown and then stranded.
        Assert.Contains("CertificateChosenDlg", dialogs);
        var chosen = package.Rows("SELECT `Control`, `Text` FROM `Control` WHERE `Dialog_` = 'CertificateChosenDlg'", 2);
        Assert.Contains(chosen, row => row[1] == "[TLSCERTIFICATEPATH]");
        Assert.Contains(chosen, row => row[0] == "OK");
        var backFromChosen = package.Rows("SELECT `Control_`, `Event`, `Argument` FROM `ControlEvent` WHERE `Dialog_` = 'CertificateChosenDlg'", 3);
        Assert.Contains(backFromChosen, row => row[0] == "OK" && row[1] == "NewDialog" && row[2] == "ServerSettingsDlg");

        // Nothing on either page may sit outside it, and nothing may sit on top of anything else. The coordinates are
        // all written by hand, and both mistakes are invisible rather than broken: a control off the page simply is not
        // there, and one lying over another is drawn underneath it. The first was already checked here; the second was
        // not, and a checkbox added to the page duly landed on the relay address field directly below it.
        foreach (var dialog in new[] { "ServerSettingsDlg", "CertificateChosenDlg" })
        {
            var bounds = package.Rows($"SELECT `Width`, `Height` FROM `Dialog` WHERE `Dialog` = '{dialog}'", 2);
            var (pageWidth, pageHeight) = (int.Parse(bounds[0][0], CultureInfo.InvariantCulture), int.Parse(bounds[0][1], CultureInfo.InvariantCulture));

            var placed = new List<(string Control, int X, int Y, int Width, int Height)>();
            foreach (var control in package.Rows($"SELECT `Control`, `X`, `Y`, `Width`, `Height` FROM `Control` WHERE `Dialog_` = '{dialog}'", 5))
            {
                if (control.Skip(1).Any(value => !int.TryParse(value, out _)))
                {
                    continue; // the dialog's own bookkeeping rows (First, Default, Cancel) have no coordinates
                }

                placed.Add((control[0],
                    int.Parse(control[1], CultureInfo.InvariantCulture), int.Parse(control[2], CultureInfo.InvariantCulture),
                    int.Parse(control[3], CultureInfo.InvariantCulture), int.Parse(control[4], CultureInfo.InvariantCulture)));
            }

            foreach (var (control, x, y, width, height) in placed)
            {
                Assert.True(x + width <= pageWidth && y + height <= pageHeight, $"{control} sits outside {dialog}: {x},{y} {width}x{height} in {pageWidth}x{pageHeight}.");
            }

            foreach (var first in placed)
            {
                foreach (var second in placed)
                {
                    // Only pairs that share area: the separator lines are as wide as the page and are meant to be
                    // crossed by nothing, and a zero-height control overlaps nothing by definition.
                    var overlapX = Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X);
                    var overlapY = Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y);
                    if (first.Control == second.Control || overlapX <= 0 || overlapY <= 0)
                    {
                        continue;
                    }

                    // Lines are drawn across the page and controls are allowed to sit beside, not on, them: what must
                    // not happen is two controls that both hold something the reader has to read or click.
                    Assert.True(first.Control.Contains("Line", StringComparison.Ordinal) || second.Control.Contains("Line", StringComparison.Ordinal),
                        $"{first.Control} and {second.Control} overlap on {dialog} by {overlapX}x{overlapY}.");
                }
            }
        }

        // The start menu entry, pointed at the page in the installation folder rather than at a URL: the repository is
        // private, so a link to it would not help whoever runs the server.
        var shortcuts = package.Rows("SELECT `Name`, `Target` FROM `Shortcut`", 2);
        var howTo = Assert.Single(shortcuts, row => row[0] == "服务器配置说明");
        Assert.Contains("how-to-configure.txt", howTo[1], StringComparison.Ordinal);

        // --- the step that gives both servers the same key ------------------------------------------------------
        var actions = package.Rows("SELECT `Action` FROM `CustomAction`", 1).Select(row => row[0]).ToList();
        Assert.Contains(actions, action => action.Contains("Configure", StringComparison.OrdinalIgnoreCase));

        var sequence = package.Rows("SELECT `Action`, `Condition` FROM `InstallExecuteSequence`", 2);
        var configure = sequence.Where(row => row[0].Contains("Configure", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(configure);
        Assert.All(configure, row => Assert.Contains("NOT REMOVE", row[1], StringComparison.Ordinal));
    }

    /// <summary>
    /// The settings page's custom action has to be something this package's msiexec can actually load and run.
    /// </summary>
    /// <remarks>
    /// Both ways of getting this wrong are silent, and both were hit while building the page. A custom action DLL that
    /// msiexec cannot load fails in two milliseconds having run no code at all, so the action's own logging stays
    /// empty and Windows Installer reports nothing beyond "Error 2896: Executing action X failed" — there is no
    /// symptom to read, only a wizard that closes when a button is pressed. That is why these are checked on the
    /// built package: the failure cannot be seen in the sources, and cannot be seen at run time either.
    /// </remarks>
    [Fact]
    public void The_settings_page_custom_action_is_packed_for_the_installer_that_runs_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows Installer is Windows only.");
        Assert.SkipWhen(Msi is null, "No server installer has been built (run build\\installer\\server\\Build-ServerInstaller.ps1).");

        using var database = new Database(Msi!, DatabaseOpenMode.ReadOnly);

        // Which msiexec the package gets, which is what the DLL has to match. SfxCA packs a native host around the
        // managed assembly and the DTF targets pick the 32-bit host unless the project sets Platform=x64, so the
        // default here is the wrong one.
        Assert.StartsWith("x64", database.SummaryInfo.Template, StringComparison.Ordinal);

        var source = BinaryStreamOf(database, "BrowseForCertificate");
        var image = ReadBinary(database, source);

        // The native host's machine type, straight out of the DLL's own header: 0x8664 is AMD64.
        var peOffset = BitConverter.ToInt32(image, 0x3C);
        Assert.Equal(0x8664, BitConverter.ToUInt16(image, peOffset + 4));

        // And the file that says which runtime that host should start. Without it the host asks for the .NET 2.0 CLR,
        // which no longer exists anywhere, and gives up with 0x80131700 before any managed code runs — with no log
        // line either, for the same reason as above. Its name is enough: SfxCA stores the names of what it packed
        // as plain text even though it compresses the contents.
        Assert.Contains("CustomAction.config", Encoding.ASCII.GetString(image), StringComparison.Ordinal);

        // The name of the stream the action is declared against, read from the package rather than assumed.
        static string BinaryStreamOf(Database database, string action)
        {
            using var view = database.OpenView($"SELECT `Source` FROM `CustomAction` WHERE `Action` = '{action}'");
            view.Execute();
            using var record = view.Fetch();
            Assert.NotNull(record);
            return record!.GetString(1);
        }

        static byte[] ReadBinary(Database database, string name)
        {
            using var view = database.OpenView($"SELECT `Data` FROM `Binary` WHERE `Name` = '{name}'");
            view.Execute();
            using var record = view.Fetch();
            Assert.NotNull(record);
            using var stream = record!.GetStream(1);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
    }
}
