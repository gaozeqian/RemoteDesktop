using WixToolset.Dtf.WindowsInstaller;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace RemoteDesktop.Server.Installer.CustomActions;

/// <summary>
/// The three things the settings page cannot do by itself: ask for a file, fill in the addresses this machine already
/// knows, and tidy up what the boxes were left holding.
/// </summary>
/// <remarks>
/// All three are immediate actions in the user-interface sequence, which is what makes them able to talk to the page at
/// all: they run in msiexec's own process, so the properties the page wrote are simply readable, and setting one
/// changes what the page shows. A deferred action has neither.
/// </remarks>
public static class SettingsPageActions
{
    /// <summary>The port the relay listens on, kept equal to ServerSetup.DefaultRelayPort.</summary>
    private const int RelayPort = 5082;

    /// <summary>
    /// Runs an action and makes sure a failure inside it says why.
    /// </summary>
    /// <remarks>
    /// Windows Installer's own report for a failed custom action names the action and nothing else (error 2896), and
    /// DTF does not put the exception into the log either, so an action that throws is indistinguishable from one that
    /// never started. This is what makes the difference visible — for the failures that happen inside the action.
    ///
    /// It is worth knowing what it cannot do, because it cost a day: it says nothing at all when the action is never
    /// entered. That is the case when msiexec cannot load the DLL (packed for the wrong architecture) or when the host
    /// cannot start a runtime (no CustomAction.config in the package) — the action then "fails" in two milliseconds
    /// with an empty log, and no amount of catching inside this file will ever see it. Those two are held in place by
    /// <c>The_settings_page_custom_action_is_packed_for_the_installer_that_runs_it</c> instead, which checks the
    /// built package.
    /// </remarks>
    private static ActionResult Run(Session session, Func<ActionResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            session.Log($"Settings page action failed: {ex}");
            return ActionResult.Failure;
        }
    }

    [CustomAction]
    public static ActionResult BrowseForCertificate(Session session) => Run(session, () => Browse(session));

    /// <summary>
    /// Asks for a certificate file and puts the answer where the settings page shows it.
    /// </summary>
    /// <remarks>
    /// Windows Installer's own browse dialog picks folders, not files, which is why the field for the certificate path
    /// was a plain text box with a path nobody can remember. This is the whole reason the action exists.
    /// </remarks>
    private static ActionResult Browse(Session session)
    {
        var current = session["TLSCERTIFICATEFILE"];
        if (string.IsNullOrWhiteSpace(current) || current.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            current = string.Empty;
        }

        var file = new string('\0', 1024);

        // Straight at comdlg32 rather than through System.Windows.Forms: the framework's file dialog lives in the GAC,
        // and a custom action is loaded inside msiexec where that failed to resolve — the action then returned
        // "success" in two milliseconds without ever showing a window, because DTF reports a failure it swallowed as
        // success. A native call has no assembly to find.
        var dialog = new OpenFileName
        {
            Size = Marshal.SizeOf<OpenFileName>(),
            // The wizard, so the dialog opens in front of it instead of behind. A custom action has no window handle
            // handed to it; the wizard is what the user just clicked on, so it is the foreground window.
            Owner = GetForegroundWindow(),
            Filter = "证书文件 (*.pfx;*.p12)\0*.pfx;*.p12\0所有文件 (*.*)\0*.*\0\0",
            FilterIndex = 1,
            File = file,
            MaxFile = file.Length,
            Title = "选择证书文件（.pfx）",
            Flags = OfnExplorer | OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir,
        };

        if (current.Length > 0)
        {
            dialog.InitialDirectory = Path.GetDirectoryName(current);
            dialog.File = Path.GetFileName(current) + new string('\0', 1024 - Path.GetFileName(current).Length);
        }

        if (GetOpenFileNameW(ref dialog))
        {
            session["TLSCERTIFICATEFILE"] = dialog.File.Split('\0')[0];
            // Read by the page to decide whether to show the chosen file: cancelling leaves the property alone, and
            // there is nothing to confirm.
            session["TLSCERTIFICATECHOSEN"] = "1";
        }
        else
        {
            session["TLSCERTIFICATECHOSEN"] = string.Empty;
        }

        return ActionResult.Success;
    }

    private const int OfnExplorer = 0x00080000;
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnPathMustExist = 0x00000800;
    private const int OfnNoChangeDir = 0x00000008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int Size;
        public nint Owner;
        public nint Instance;
        public string Filter;
        public string CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public string File;
        public int MaxFile;
        public string FileTitle;
        public int MaxFileTitle;
        public string InitialDirectory;
        public string Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public string DefaultExtension;
        public nint CustomData;
        public nint Hook;
        public string TemplateName;
        public nint Reserved;
        public int Reserved2;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OpenFileName dialog);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    /// <summary>
    /// Fills in the two addresses that this machine already knows, before the settings page is shown.
    /// </summary>
    /// <remarks>
    /// A connection string and an address are shapes nobody types correctly from memory, and an empty box next to a
    /// warning teaches nothing — so the box arrives holding the answer for this machine, which the person installing
    /// can then correct. It is only the starting point: whatever is left there is what the servers are configured with,
    /// and the database is tried before anything is installed.
    /// </remarks>
    [CustomAction]
    public static ActionResult PrefillSettings(Session session) => Run(session, () => Prefill(session));

    private static ActionResult Prefill(Session session)
    {
        // Only while the value is still the one the package ships. This runs on the way into the page, and leaving the
        // page and coming back must not undo what was typed there.
        if (IsUnset(session["RELAYPUBLICENDPOINT"]))
        {
            session["RELAYPUBLICENDPOINT"] = $"{LocalAddress()}:{RelayPort}";
        }

        if (IsUnset(session["POSTGRESCONNECTIONSTRING"]))
        {
            // The same shape the page used to describe in a note, now filled in: nothing about it is specific to this
            // machine except that the database is expected to be on it, which is the common case and the one worth
            // spelling out. The password is the one a fresh local PostgreSQL is set up with, so the whole string can be
            // accepted as it stands on the machine most people are installing onto.
            session["POSTGRESCONNECTIONSTRING"] = $"Host=127.0.0.1;Port=5432;Database=remotedesktop;Username=postgres;Password=postgres";
        }

        return ActionResult.Success;
    }

    /// <summary>
    /// Tidies what the boxes on the settings page were left holding, on the way out of it.
    /// </summary>
    /// <remarks>
    /// Two jobs, both about the space that the boxes start with.
    ///
    /// The password is trimmed. That box arrives holding a single space rather than nothing, because a property with no
    /// value never reaches the property table and a control bound to it comes out greyed and untypable — so anybody who
    /// clicks into the box and types their password without clearing it first ends up with a space in front of it, and
    /// a .pfx opened with " secret" instead of "secret" simply fails to open. Trimming is the only sane reading of a
    /// box whose way of saying "no password" is a space; the cost is that a password that genuinely begins or ends with
    /// one cannot be given here.
    ///
    /// The other three boxes are put back to a space when they are left empty. Not cosmetic: a box the user clears has
    /// its property removed, and if they then go back a page and return, the control is built again against a property
    /// that is no longer there and comes out greyed — with no way left to type the value back in.
    /// </remarks>
    [CustomAction]
    public static ActionResult NormalizeSettings(Session session) => Run(session, () => Normalize(session));

    private static ActionResult Normalize(Session session)
    {
        var password = (session["TLSCERTIFICATEPASSWORD"] ?? string.Empty).Trim();
        session["TLSCERTIFICATEPASSWORD"] = password.Length == 0 ? Space : password;

        foreach (var name in new[] { "TLSCERTIFICATEFILE", "POSTGRESCONNECTIONSTRING", "RELAYPUBLICENDPOINT" })
        {
            if (string.IsNullOrWhiteSpace(session[name]))
            {
                session[name] = Space;
            }
        }

        return ActionResult.Success;
    }

    /// <summary>What a box that is meant to be empty holds: invisible, and still a value the installer will keep.</summary>
    private const string Space = " ";

    /// <summary>Whether a property still holds what the package ships, rather than anything a person chose.</summary>
    /// <remarks>
    /// The null test is written out rather than left to <c>string.IsNullOrWhiteSpace</c>: on .NET Framework that method
    /// carries no nullable annotation, so the compiler cannot tell that the value is not null afterwards and refuses
    /// the dereference below.
    /// </remarks>
    private static bool IsUnset(string? value) =>
        value is null || value.Trim().Length == 0 || value.Equals("auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// This machine's first address that is not loopback, or loopback when it has none.
    /// </summary>
    /// <remarks>
    /// The same rule the servers apply to "auto" themselves, and it has to stay the same: the page shows what auto
    /// would have chosen, so that accepting it and typing "auto" mean one thing. See LocalAddress in ServerSetup.cs.
    /// </remarks>
    private static IPAddress LocalAddress()
    {
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var address in network.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
                {
                    return address.Address;
                }
            }
        }

        return IPAddress.Loopback;
    }

}
