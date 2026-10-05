using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteDesktop.Platform.Windows.Tests;

/// <summary>
/// What the built MSI really contains, read through the Windows Installer API (no installation, no elevation).
/// </summary>
/// <remarks>
/// This exists because WiX 5.0.2 drops a section that holds no component when linking, without a word: an installer
/// built that way looks fine, builds without warnings, and quietly does nothing on the custom-action side. ICE
/// validation does not catch it either, because nothing in the database is inconsistent - things are simply absent.
/// <para>
/// The test skips unless an installer has been built (build\installer\windows\build-installer.ps1), or unless the path
/// is given in RD_INSTALLER_MSI.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class InstallerPackageTests
{
    private const string FileTableQuery = "SELECT `File`, `FileName` FROM `File`";

    private static readonly string? Msi = FindInstaller();

    private static string? FindInstaller()
    {
        var given = Environment.GetEnvironmentVariable("RD_INSTALLER_MSI");
        if (!string.IsNullOrEmpty(given))
        {
            return File.Exists(given) ? given : null;
        }

        // The repository root is the folder with the solution file; the installer ends up in its artifacts folder.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RemoteDesktop.slnx")))
        {
            directory = directory.Parent;
        }

        var artifacts = directory is null ? null : Path.Combine(directory.FullName, "build", "installer", "windows", "artifacts");
        return artifacts is null || !Directory.Exists(artifacts)
            ? null
            : Directory.GetFiles(artifacts, "*.msi").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    [Fact]
    public void The_package_contains_the_payload_the_service_and_the_machine_setup_actions()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows Installer is Windows only.");
        Assert.SkipWhen(Msi is null, "No installer has been built (run build\\installer\\windows\\build-installer.ps1).");
        using var package = new Package(Msi!);

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
        var services = package.Rows("SELECT `Name`, `StartType` FROM `ServiceInstall`", 2);
        var service = Assert.Single(services);
        Assert.Equal("RemoteDesktopHost", service[0]);
        Assert.Equal("2", service[1]); // automatic
        Assert.Single(package.Rows("SELECT `Name` FROM `ServiceControl`", 1));

        // --- firewall and the start menu shortcut ----------------------------------------------------------------
        Assert.Single(package.Rows("SELECT `Name` FROM `Wix5FirewallException`", 1));
        Assert.Single(package.Rows("SELECT `Name` FROM `Shortcut`", 1));
        Assert.Single(package.Rows("SELECT `Root` FROM `Registry`", 1)); // the shortcut's per-user key path

        // --- the custom actions that set the machine up and clean it up ------------------------------------------
        var actions = package.Rows("SELECT `Action`, `Type`, `Source`, `Target` FROM `CustomAction`", 4);

        // These run through System32\cmd.exe rather than through the service executable itself. That is not a style
        // choice: a type 18 action (the installed file as the target) fails the whole installation with error 1721 when
        // Windows cannot start that file - a leftover process holding it, or a pending rename waiting for a reboot -
        // and "ignore" does not cover a launch failure. It only covers a non-zero exit code, which is what cmd.exe
        // gives us instead. An upgrade failed exactly that way, so this is checked here.
        foreach (var name in new[] { "ConfigureSystem", "CleanupKeepData", "CleanupRemoveData" })
        {
            var only = Assert.Single(actions, row => row[0] == name);
            var type = int.Parse(only[1], CultureInfo.InvariantCulture);
            Assert.Equal(34, type & 0x3F);          // exe from a directory
            Assert.Equal(64, type & 64);            // ignore the exit code
            Assert.Equal("SystemFolder", only[2]);
            Assert.Contains("cmd.exe", only[3], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("RemoteDesktop.HostService.exe", only[3], StringComparison.Ordinal);
        }

        // Leftover processes are closed before validation, which is when Windows decides whether a restart is needed.
        var stopLeftovers = Assert.Single(actions, row => row[0] == "StopLeftoverProcesses");
        Assert.Contains("taskkill", stopLeftovers[3], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RemoteDesktop.SessionAgent.exe", stopLeftovers[3], StringComparison.Ordinal);
        Assert.Single(package.Rows("SELECT `Action` FROM `InstallExecuteSequence`", 1), row => row[0] == "StopLeftoverProcesses");

        var sequence = package.Rows("SELECT `Action`, `Condition` FROM `InstallExecuteSequence`", 2);
        var configure = Assert.Single(sequence, row => row[0] == "ConfigureSystem");
        var keep = Assert.Single(sequence, row => row[0] == "CleanupKeepData");
        var remove = Assert.Single(sequence, row => row[0] == "CleanupRemoveData");

        // "NOT REMOVE" and nothing more: adding UPGRADINGPRODUCTCODE here made the *old* product run this action while
        // it was being removed during an upgrade, with its files already gone - error 1721 and a rolled-back install.
        Assert.Equal("NOT REMOVE", configure[1]);

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
        Assert.Equal("0", properties["REMOVEDATA"]); // keeping the device identity is the default
        foreach (var name in new[] { "RENDEZVOUSURI", "REMOVEDATA" })
        {
            Assert.Contains(name, properties["SecureCustomProperties"], StringComparison.Ordinal);
        }

        // --- upgrades, folders, interface ------------------------------------------------------------------------
        Assert.NotEmpty(package.Rows("SELECT `UpgradeCode` FROM `Upgrade`", 1));
        Assert.Contains("INSTALLFOLDER", package.Rows("SELECT `Directory` FROM `Directory`", 1).Select(row => row[0]));
        // The dialog table's key column is Dialog (not the Dialog_ of WiX 3).
        var dialogs = package.Rows("SELECT `Dialog` FROM `Dialog`", 1).Select(row => row[0]).ToList();
        Assert.True(dialogs.Count >= 8, $"The installer has no user interface ({dialogs.Count} dialogs).");

        // The license page shows the agreement text (License.txt turned into RTF by the build); a missing page would mean
        // the wizard silently skips it.
        Assert.Contains("LicenseAgreementDlg", dialogs);
        Assert.Contains("InstallDirDlg", dialogs);

        // And the text itself travels in the package: without it the dialog would come up empty. WiX 5 puts it straight
        // into the control that shows it (a WixUI extension that inlines the RTF) rather than into the Binary table.
        var license = package.Rows(
            "SELECT `Text` FROM `Control` WHERE `Dialog_` = 'LicenseAgreementDlg' AND `Control` = 'LicenseText'", 1);
        var text = Assert.Single(license)[0];
        Assert.StartsWith(@"{\rtf1", text, StringComparison.Ordinal);

        // Chinese is written as RTF Unicode escapes (\uN?), which is what makes it survive any code page. A BMP code
        // point above 32767 is written as a negative number, so the first escape being negative means CJK text.
        var escape = System.Text.RegularExpressions.Regex.Match(text, @"\\u(-?\d+)\?");
        Assert.True(escape.Success, "The license text has no Unicode escapes; it is probably not the one built from License.txt.");
        Assert.True(int.Parse(escape.Groups[1].Value, CultureInfo.InvariantCulture) < 0, "The license text does not start with Chinese text.");
    }

    /// <summary>The installer database, opened read-only through the Windows Installer API.</summary>
    private sealed class Package : IDisposable
    {
        private readonly object _database;

        public Package(string path)
        {
            var type = Type.GetTypeFromProgID("WindowsInstaller.Installer")
                ?? throw new InvalidOperationException("Windows Installer is not available.");
            var installer = Activator.CreateInstance(type)!;
            _database = installer.GetType().InvokeMember("OpenDatabase", BindingFlags.InvokeMethod, null, installer, [path, 0], CultureInfo.InvariantCulture)!;
        }

        /// <summary>The rows of a query as string arrays; an empty list when the table does not exist.</summary>
        /// <remarks>The installer's SQL subset has no LIKE, so tables are read whole and filtered by the caller.</remarks>
        public List<string[]> Rows(string sql, int columns)
        {
            object view;
            try
            {
                view = _database.GetType().InvokeMember("OpenView", BindingFlags.InvokeMethod, null, _database, [sql], CultureInfo.InvariantCulture)!;
            }
            catch (TargetInvocationException)
            {
                return [];
            }

            view.GetType().InvokeMember("Execute", BindingFlags.InvokeMethod, null, view, [null], CultureInfo.InvariantCulture);
            var rows = new List<string[]>();
            while (true)
            {
                object? record;
                try
                {
                    record = view.GetType().InvokeMember("Fetch", BindingFlags.InvokeMethod, null, view, null, CultureInfo.InvariantCulture);
                }
                catch (COMException)
                {
                    break; // no more rows
                }

                if (record is null)
                {
                    break;
                }

                var row = new string[columns];
                for (var i = 1; i <= columns; i++)
                {
                    row[i - 1] = Read(record, i);
                }

                rows.Add(row);
            }

            return rows;
        }

        // A column holds text or a number; anything missing comes back as an empty string.
        private static string Read(object record, int index)
        {
            try
            {
                return (string)record.GetType().InvokeMember("StringData", BindingFlags.GetProperty, null, record, [index], CultureInfo.InvariantCulture)!;
            }
            catch (TargetInvocationException)
            {
            }

            try
            {
                return Convert.ToString(record.GetType().InvokeMember("IntegerData", BindingFlags.GetProperty, null, record, [index], CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)!;
            }
            catch (TargetInvocationException)
            {
                return string.Empty;
            }
        }

        public void Dispose()
        {
            // The database closes when it is collected; there is nothing to release explicitly.
        }
    }
}
