using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using RemoteDesktop.Application;
using WixToolset.Dtf.WindowsInstaller;

namespace RemoteDesktop.Platform.Windows.UnitTests;

/// <summary>
/// What the built MSI really contains, read through the Windows Installer API (no installation, no elevation).
/// </summary>
/// <remarks>
/// This exists because WiX 5.0.2 drops a section that holds no component when linking, without a word: an installer
/// built that way looks fine, builds without warnings, and quietly does nothing on the custom-action side. ICE
/// validation does not catch it either, because nothing in the database is inconsistent - things are simply absent.
/// <para>
/// The test skips unless an installer has been built (build\installer\windows\client\Build-ClientInstaller.ps1), or unless the path
/// is given in RD_INSTALLER_MSI.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class InstallerPackageTests
{
    private const string FileTableQuery = "SELECT `File`, `FileName` FROM `File`";

    /// <summary>The components that carry a certificate into a machine's trusted roots, in the order they are described above.</summary>
    private static readonly string[] CertificateComponents = ["TrustedRootCert", "TrustedServerCert"];

    private static readonly string? Msi = FindInstaller();

    private static string? FindInstaller()
    {
        var given = Environment.GetEnvironmentVariable("RD_INSTALLER_MSI");
        if (!string.IsNullOrEmpty(given))
        {
            return File.Exists(given) ? given : null;
        }

        // The repository root is the folder with the solution file; installers land under its single artifacts/ root
        // (see CONTRIBUTING.md, "构建产物布局"). The client and the server share one directory because the role is
        // already in the file name.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RemoteDesktop.slnx")))
        {
            directory = directory.Parent;
        }

        var artifacts = directory is null ? null : Path.Combine(directory.FullName, "artifacts", "installer", "windows");
        return artifacts is null || !Directory.Exists(artifacts)
            ? null
            : Directory.GetFiles(artifacts, "RemoteDesktop-Client-Windows-*.msi").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    /// <summary>
    /// Settings > Apps reads ARPPRODUCTICON, and the shortcut names the same icon rather than relying on the target
    /// file's embedded one. Neither is visible in a source review of the assets; only the built package says whether
    /// the icon table actually made it in.
    /// </summary>
    [Fact]
    public void The_package_carries_the_product_icon()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows Installer is Windows only.");
        Assert.SkipWhen(Msi is null, "No installer has been built (run build\\installer\\windows\\client\\Build-ClientInstaller.ps1).");
        using var package = new MsiPackage(Msi!);

        Assert.Contains(package.Rows("SELECT `Name` FROM `Icon`", 1), row => row[0] == "AppIcon");
        Assert.Equal("AppIcon", Assert.Single(package.Rows("SELECT `Value` FROM `Property` WHERE `Property` = 'ARPPRODUCTICON'", 1))[0]);
        Assert.Contains(package.Rows("SELECT `Icon_` FROM `Shortcut`", 1), row => row[0] == "AppIcon");
    }

    /// <summary>
    /// The <c>remotedesktop://</c> scheme is a registry row, so a source review cannot show whether it made it into the
    /// package. Windows starts the client with the URL as its argument; without these rows a link does nothing at all.
    /// </summary>
    [Fact]
    public void The_package_registers_the_connection_scheme()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows Installer is Windows only.");
        Assert.SkipWhen(Msi is null, "No installer has been built (run build\\installer\\windows\\client\\Build-ClientInstaller.ps1).");
        using var package = new MsiPackage(Msi!);

        // The Registry table's Root column is 0 = HKCR, 1 = HKCU, 2 = HKLM: the scheme belongs to the installed
        // program, so it has to be machine-wide rather than only for whoever happened to install it.
        var scheme = package.Rows("SELECT `Name`, `Value`, `Root` FROM `Registry` WHERE `Key` = 'Software\\Classes\\remotedesktop'", 3);
        Assert.Contains(scheme, row => row[0] == "URL Protocol" && row[2] == "2");

        var command = Assert.Single(
            package.Rows("SELECT `Value` FROM `Registry` WHERE `Key` = 'Software\\Classes\\remotedesktop\\shell\\open\\command'", 1))[0];
        Assert.Contains("RemoteDesktop.Client.exe", command, StringComparison.Ordinal);
        Assert.Contains("%1", command, StringComparison.Ordinal);
    }

    /// <summary>
    /// The number Settings > Apps shows is the number the build was made from, plus the fourth part MSI requires.
    /// Nothing else checks that: the build script computes it, and a package left over from an earlier build would
    /// happily sit in artifacts/ and be reviewed as if it were this one.
    /// </summary>
    [Fact]
    public void The_package_version_is_the_product_version_in_four_parts()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows Installer is Windows only.");
        Assert.SkipWhen(Msi is null, "No installer has been built (run build\\installer\\windows\\client\\Build-ClientInstaller.ps1).");
        using var package = new MsiPackage(Msi!);

        var productVersion = Assert.Single(
            package.Rows("SELECT `Value` FROM `Property` WHERE `Property` = 'ProductVersion'", 1))[0];

        Assert.Equal($"{ProductVersion.Display}.0", productVersion);
    }

    [Fact]
    public void The_package_contains_the_payload_the_service_and_the_machine_setup_actions()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows Installer is Windows only.");
        Assert.SkipWhen(Msi is null, "No installer has been built (run build\\installer\\windows\\client\\Build-ClientInstaller.ps1).");
        using var package = new MsiPackage(Msi!);

        // --- payload ---------------------------------------------------------------------------------------------
        var files = package.Rows(FileTableQuery, 2);
        // A floor rather than an exact number: the point is to catch an empty or truncated payload, and the count moves
        // with the runtime and whether symbol files are shipped (-KeepSymbols).
        Assert.True(files.Count >= 250, $"Only {files.Count} files in the payload.");

        // File names are stored as "short|long" because the package carries 8.3 names as well.
        var longNames = files.Select(row => row[1].Split('|')[^1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var expected in new[] { "RemoteDesktop.Client.exe", "RemoteDesktop.HostService.exe", "RemoteDesktop.SessionAgent.exe", "appsettings.json", "msquic.dll" })
        {
            Assert.Contains(expected, longNames);
        }

        // --- the service -----------------------------------------------------------------------------------------
        // The service restarts itself when it crashes: a machine that is meant to be reachable without anybody in
        // front of it must not stay down because nobody noticed. README.md describes this, and a service that is
        // installed without it looks exactly the same until the day it matters.
        // The column is `ServiceName`: asking for `Name` makes the query fail in a way this reader turns into "no
        // rows at all", which reads exactly like a package that is missing the thing being looked for.
        var restarts = package.Rows(
            "SELECT `ServiceName`, `FirstFailureActionType`, `SecondFailureActionType`, `ThirdFailureActionType`, `RestartServiceDelayInSeconds` FROM `Wix4ServiceConfig`",
            5);
        var host = Assert.Single(restarts, row => row[0] == "RemoteDesktopHost");
        Assert.Equal("restart", host[1]);
        Assert.Equal("restart", host[2]);
        Assert.Equal("restart", host[3]);
        Assert.Equal("5", host[4]);

        var services = package.Rows("SELECT `Name`, `StartType` FROM `ServiceInstall`", 2);
        var service = Assert.Single(services);
        Assert.Equal("RemoteDesktopHost", service[0]);
        Assert.Equal("2", service[1]); // automatic
        Assert.Single(package.Rows("SELECT `Name` FROM `ServiceControl`", 1));

        // --- firewall and the start menu shortcut ----------------------------------------------------------------
        Assert.Single(package.Rows("SELECT `Name` FROM `Wix5FirewallException`", 1));
        Assert.Single(package.Rows("SELECT `Name` FROM `Shortcut`", 1));

        // Registry values are the key paths of the two components that have no file of their own to be keyed by: the
        // per-user shortcut (1 = HKCU), and - in a build that signed with a self-signed certificate - the copy of that
        // certificate the machine keeps (2 = HKLM), which is checked with the certificate itself below.
        var registry = package.Rows("SELECT `Root`, `Name` FROM `Registry`", 2);
        Assert.Single(registry, row => row[0] == "1" && row[1] == "StartMenuShortcut");

        // --- the client package is also the controlled end, so it refuses the host-only package ------------------
        // One machine has one RemoteDesktopHost service and one device identity. Installing the host-only package
        // alongside this one would be a second service for the same pipe and code, so each package blocks the other;
        // the row is checked because an Upgrade element that never made it into the package blocks nothing.
        var upgrades = package.Rows("SELECT `UpgradeCode` FROM `Upgrade`", 1);
        // Windows Installer stores a GUID in braces; the WiX source spells the same value without them.
        Assert.Contains(upgrades, row =>
            string.Equals(row[0].Trim('{', '}'), "3d9e7b41-6c2a-4f18-8b57-2e4c1a9d6f03", StringComparison.OrdinalIgnoreCase));
        var launch = package.Rows("SELECT `Condition` FROM `LaunchCondition`", 1);
        Assert.Contains(launch, row => row[0].Contains("HOSTPACKAGEINSTALLED", StringComparison.Ordinal));

        // --- the certificates that go into a machine's trusted roots ----------------------------------------------
        // Two can be in a package, and either, both or neither may be: the certificate the build signed with, when it
        // is self-signed (Build-ClientInstaller.ps1 passes -p:TrustedRootCertificate, which is the only thing that compiles
        // that component in; it is what lets a UIAccess agent signed with it start without a prompt), and the
        // signaling server's, when the build was given one with -ServerCertificate (what makes wss:// work against a
        // server no authority issued a certificate for). At install time the IIS extension's action adds each to the
        // machine's trusted roots.
        //
        // The pieces travel together or not at all - which is what this checks, because each of them alone is a
        // silently broken package: a certificate row whose binary is empty installs something that is not a
        // certificate as a trusted root (an unsigned build used to bake in an empty placeholder exactly like that),
        // and a certificate in a component with no key path is one that nothing removes again.
        var certificates = package.Rows("SELECT `Component_`, `StoreLocation`, `StoreName`, `Binary_` FROM `Wix4Certificate`", 4);
        var trustedRoots = registry.Where(row => row[1].EndsWith("CertInstalled", StringComparison.Ordinal)).ToList();
        // How many certificates the build meant to put in, passed by Build-ClientInstaller.ps1 along with the path to the
        // package. Without it this only checks that a certificate's pieces agree with each other, which a package
        // that quietly lost the whole component passes too - the component and its key path are simply both absent.
        // With it, "the build asked for one and the package has none" fails here instead of on somebody's machine.
        var expectedCertificates = Environment.GetEnvironmentVariable("RD_EXPECTED_CERTIFICATES");
        if (!string.IsNullOrEmpty(expectedCertificates))
        {
            Assert.Equal(int.Parse(expectedCertificates, CultureInfo.InvariantCulture), certificates.Count);
        }

        foreach (var component in CertificateComponents)
        {
            // The component and its key path are there together: a certificate in a component with no key path is one
            // that nothing removes again.
            Assert.Equal(
                certificates.Any(row => row[0] == component),
                trustedRoots.Any(row => row[1] == $"{component}Installed"));
        }

        foreach (var certificate in certificates)
        {
            Assert.Contains(certificate[0], CertificateComponents);
            Assert.Equal("2", certificate[1]); // the local machine, not the user who installs
            Assert.Equal("Root", certificate[2]); // and its trusted root store

            var binary = package.Rows($"SELECT `Data` FROM `Binary` WHERE `Name` = '{certificate[3]}'", 1);
            Assert.True(
                int.Parse(Assert.Single(binary)[0], CultureInfo.InvariantCulture) >= 100,
                $"The certificate in {certificate[0]} is empty.");
        }

        // --- the custom actions that set the machine up and clean it up ------------------------------------------
        var actions = package.Rows("SELECT `Action`, `Type`, `Source`, `Target` FROM `CustomAction`", 4);

        // These run through System32\cmd.exe rather than through the service executable itself. That is not a style
        // choice: a type 18 action (the installed file as the target) fails the whole installation with error 1721 when
        // Windows cannot start that file - a leftover process holding it, or a pending rename waiting for a reboot -
        // and "ignore" does not cover a launch failure. It only covers a non-zero exit code, which is what cmd.exe
        // gives us instead.
        foreach (var name in new[] { "ConfigureSystem", "CleanupKeepData", "CleanupRemoveData" })
        {
            var only = Assert.Single(actions, row => row[0] == name);
            var type = int.Parse(only[1], CultureInfo.InvariantCulture);
            Assert.Equal(34, type & 0x3F);          // exe from a directory
            Assert.Equal("SystemFolder", only[2]);
            Assert.Contains("cmd.exe", only[3], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("RemoteDesktop.HostService.exe", only[3], StringComparison.Ordinal);
        }

        // ConfigureSystem must report a failed address/certificate write; the cleanup steps are best-effort. The
        // service itself catches the SAS-policy and restart failures, so a non-zero exit really does mean "the
        // settings were not applied".
        var configureAction = Assert.Single(actions, row => row[0] == "ConfigureSystem");
        Assert.Equal(0, int.Parse(configureAction[1], CultureInfo.InvariantCulture) & 64);
        Assert.Contains("--rendezvous-certificate", configureAction[3], StringComparison.Ordinal);
        Assert.Contains("RENDEZVOUSCERTIFICATEPATH", configureAction[3], StringComparison.Ordinal);
        foreach (var name in new[] { "CleanupKeepData", "CleanupRemoveData" })
        {
            Assert.Equal(64, int.Parse(Assert.Single(actions, row => row[0] == name)[1], CultureInfo.InvariantCulture) & 64);
        }

        // The connection page's actions: three immediate page actions and the execute-sequence certificate export.
        foreach (var name in new[]
                 {
                     "PrefillConnectionSettings", "ValidateConnectionSettings", "BrowseForServerCertificate",
                     "PrepareRendezvousCertificate",
                 })
        {
            var action = Assert.Single(actions, row => row[0] == name);
            Assert.Equal("ClientActions", action[2]);
            Assert.Equal(1, int.Parse(action[1], CultureInfo.InvariantCulture) & 0x3F); // managed DLL
            Assert.Equal(0, int.Parse(action[1], CultureInfo.InvariantCulture) & 0x400); // immediate
        }

        // Leftover processes are closed before validation, which is when Windows decides whether a restart is needed.
        var stopLeftovers = Assert.Single(actions, row => row[0] == "StopLeftoverProcesses");
        Assert.Contains("taskkill", stopLeftovers[3], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RemoteDesktop.SessionAgent.exe", stopLeftovers[3], StringComparison.Ordinal);
        Assert.Single(package.Rows("SELECT `Action` FROM `InstallExecuteSequence`", 1), row => row[0] == "StopLeftoverProcesses");

        var sequence = package.Rows("SELECT `Action`, `Condition`, `Sequence` FROM `InstallExecuteSequence`", 3);
        var configure = Assert.Single(sequence, row => row[0] == "ConfigureSystem");
        var keep = Assert.Single(sequence, row => row[0] == "CleanupKeepData");
        var remove = Assert.Single(sequence, row => row[0] == "CleanupRemoveData");

        // A fresh install always applies the connection values; an upgrade or repair applies them only when the
        // connection page was actually used (CONNECTIONEDITED), so a silent upgrade keeps the live settings. Adding
        // UPGRADINGPRODUCTCODE here would make the *old* product run this action while it was being removed, with its
        // files already gone - error 1721 and a rolled-back install.
        Assert.Contains("NOT REMOVE", configure[1], StringComparison.Ordinal);
        Assert.Contains("NOT WIX_UPGRADE_DETECTED", configure[1], StringComparison.Ordinal);
        Assert.Contains("NOT Installed", configure[1], StringComparison.Ordinal);
        Assert.Contains("CONNECTIONEDITED", configure[1], StringComparison.Ordinal);

        var prepareCertificate = Assert.Single(actions, row => row[0] == "PrepareRendezvousCertificate");
        Assert.Equal("ClientActions", prepareCertificate[2]);
        var prepareSequence = Assert.Single(sequence, row => row[0] == "PrepareRendezvousCertificate");
        Assert.Contains("CONNECTIONEDITED", prepareSequence[1], StringComparison.Ordinal);
        Assert.True(int.Parse(prepareSequence[2], CultureInfo.InvariantCulture) < int.Parse(configure[2], CultureInfo.InvariantCulture),
            "PrepareRendezvousCertificate must run before ConfigureSystem.");

        // Nothing may be sequenced between InstallInitialize and RemoveExistingProducts: Windows Installer rejects the
        // upgrade with error 2613 when the removal is not the first action after InstallInitialize, which is why the
        // live settings are not migrated out of Program Files by an action in that gap.
        Assert.Equal("1501", Assert.Single(sequence, row => row[0] == "RemoveExistingProducts")[2]);

        // The two uninstall paths only differ in what they do with the machine's data, and neither runs while a major
        // upgrade removes the old product: that would wipe the device identity on every upgrade.
        foreach (var row in new[] { keep, remove })
        {
            Assert.Contains("REMOVE~=\"ALL\"", row[1], StringComparison.Ordinal);
            Assert.Contains("NOT UPGRADINGPRODUCTCODE", row[1], StringComparison.Ordinal);
            Assert.Contains("REMOVEDATA", row[1], StringComparison.Ordinal);
        }

        Assert.Contains("REMOVEDATA<>\"1\"", keep[1], StringComparison.Ordinal);
        Assert.Contains("REMOVEDATA=\"1\"", remove[1], StringComparison.Ordinal);

        // --- install-time properties -----------------------------------------------------------------------------
        var properties = package.Rows("SELECT `Property`, `Value` FROM `Property`", 2).ToDictionary(row => row[0], row => row[1]);
        Assert.True(properties.ContainsKey("RENDEZVOUSURI"), "The server address cannot be given at install time.");
        Assert.True(properties.ContainsKey("SERVERCERTIFICATEPATH"), "The server certificate cannot be given at install time.");
        Assert.True(properties.ContainsKey("RENDEZVOUSCERTIFICATEPATH"), "The pinned certificate path is missing.");
        Assert.True(properties.ContainsKey("DEFAULT_RENDEZVOUSURI"), "The package default address is not recorded.");
        Assert.Equal("1", properties["REMOVEDATA"]); // an uninstall removes the device identity unless REMOVEDATA=0
        Assert.Equal("0", properties["CONNECTIONEDITED"]);
        foreach (var name in new[] { "RENDEZVOUSURI", "REMOVEDATA", "SERVERCERTIFICATEPATH", "RENDEZVOUSCERTIFICATEPATH" })
        {
            Assert.Contains(name, properties["SecureCustomProperties"], StringComparison.Ordinal);
        }

        // --- upgrades, folders, interface ------------------------------------------------------------------------
        Assert.NotEmpty(package.Rows("SELECT `UpgradeCode` FROM `Upgrade`", 1));
        Assert.Contains("INSTALLFOLDER", package.Rows("SELECT `Directory` FROM `Directory`", 1).Select(row => row[0]));
        // The dialog table's key column is Dialog (not the Dialog_ of WiX 3).
        var dialogs = package.Rows("SELECT `Dialog` FROM `Dialog`", 1).Select(row => row[0]).ToList();
        Assert.True(dialogs.Count >= 8, $"The installer has no user interface ({dialogs.Count} dialogs).");

        // The license page shows the agreement text (the repository's LICENSE turned into RTF by the build); a missing
        // page would mean the wizard silently skips it.
        Assert.Contains("LicenseAgreementDlg", dialogs);
        Assert.Contains("InstallDirDlg", dialogs);
        Assert.Contains("ConnectionDlg", dialogs);
        Assert.Contains("ServerCertificateChosenDlg", dialogs);
        Assert.Contains("ConnectionErrorDlg", dialogs);

        var connectionControls = package.Rows(
            "SELECT `Control` FROM `Control` WHERE `Dialog_` = 'ConnectionDlg'", 1).Select(row => row[0]).ToList();
        foreach (var control in new[] { "AddressEdit", "CertificateEdit", "CertificateBrowse" })
        {
            Assert.Contains(control, connectionControls);
        }

        // The connection page is for a fresh install or a major upgrade, never a repair: a repair keeps the live
        // values and uses the maintenance flow's own Back transition.
        var controlEvents = package.Rows(
            "SELECT `Dialog_`, `Control_`, `Event`, `Argument`, `Condition` FROM `ControlEvent`", 5);
        Assert.Contains(controlEvents, row =>
            row[0] == "InstallDirDlg" && row[1] == "Next" && row[3] == "ConnectionDlg"
            && row[4].Contains("NOT Installed", StringComparison.Ordinal));
        Assert.Contains(controlEvents, row =>
            row[0] == "VerifyReadyDlg" && row[1] == "Back" && row[3] == "ConnectionDlg"
            && row[4].Contains("NOT Installed", StringComparison.Ordinal));

        // And the text itself travels in the package: without it the dialog would come up empty. WiX 5 puts it straight
        // into the control that shows it (a WixUI extension that inlines the RTF) rather than into the Binary table.
        var license = package.Rows(
            "SELECT `Text` FROM `Control` WHERE `Dialog_` = 'LicenseAgreementDlg' AND `Control` = 'LicenseText'", 1);
        var text = Assert.Single(license)[0];
        Assert.StartsWith(@"{\rtf1", text, StringComparison.Ordinal);

        // Chinese is written as RTF Unicode escapes (\uN?), which is what makes it survive any code page. A BMP code
        // point above 32767 is written as a negative number, so the first escape being negative means CJK text.
        var escape = System.Text.RegularExpressions.Regex.Match(text, @"\\u(-?\d+)\?");
        Assert.True(escape.Success, "The license text has no Unicode escapes; it is probably not the one built from LICENSE.");
        Assert.True(int.Parse(escape.Groups[1].Value, CultureInfo.InvariantCulture) < 0, "The license text does not start with Chinese text.");
    }

    /// <summary>
    /// The client/host package's connection-page and fixed-password actions have to be something this package's msiexec
    /// can load. Getting the bitness or the runtime wrong is silent: the action fails in two milliseconds with no
    /// managed line in the log, which is why the server package checks the same two things.
    /// </summary>
    [Fact]
    public void The_client_custom_action_is_packed_for_the_installer_that_runs_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows Installer is Windows only.");
        Assert.SkipWhen(Msi is null, "No installer has been built (run build\\installer\\windows\\client\\Build-ClientInstaller.ps1).");

        using var database = new Database(Msi!, DatabaseOpenMode.ReadOnly);

        Assert.StartsWith("x64", database.SummaryInfo.Template, StringComparison.Ordinal);

        using var view = database.OpenView("SELECT `Source` FROM `CustomAction` WHERE `Action` = 'PrefillConnectionSettings'");
        view.Execute();
        using var action = view.Fetch();
        Assert.NotNull(action);
        var binaryName = action!.GetString(1);
        Assert.Equal("ClientActions", binaryName);

        using var binaryView = database.OpenView($"SELECT `Data` FROM `Binary` WHERE `Name` = '{binaryName}'");
        binaryView.Execute();
        using var record = binaryView.Fetch();
        Assert.NotNull(record);
        using var stream = record!.GetStream(1);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var image = memory.ToArray();

        var peOffset = BitConverter.ToInt32(image, 0x3C);
        Assert.Equal(0x8664, BitConverter.ToUInt16(image, peOffset + 4)); // AMD64
        Assert.Contains("CustomAction.config", Encoding.ASCII.GetString(image), StringComparison.Ordinal);
    }
}
