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
    /// Fills in the address that this machine already knows, before the settings page is shown.
    /// </summary>
    /// <remarks>
    /// An address is a shape nobody types correctly from memory, and an empty box next to a warning teaches nothing —
    /// so the box arrives holding the answer for this machine, which the person installing can then correct.
    ///
    /// The database box is deliberately left empty. Empty means "decide for this machine", which starts a SQLite
    /// database in the local data folder; filling in a PostgreSQL template here would point almost every installation
    /// at a database server that is not there.
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
            // A space, not "auto": the box is meant to look empty (the label says "leave it empty for SQLite"), and
            // the property only has to exist for the control to be editable. NormalizeSettings turns the space into
            // "auto" on the way out of the last page, so the configuration step still reads "decide for this machine"
            // rather than an empty string, which would mean "no database at all".
            session["POSTGRESCONNECTIONSTRING"] = Space;
        }

        return ActionResult.Success;
    }

    /// <summary>
    /// Checks the listening-address box before the wizard leaves the page, the way the client's connection page checks
    /// the address it is given.
    /// </summary>
    /// <remarks>
    /// Two answers are refused here rather than by a service that dies on its first start: an address that is not an
    /// absolute http(s) URL (a typo), and an https address with TLS switched off — Kestrel cannot listen on https
    /// without a certificate, and the failure would otherwise appear only in the service log after the install said
    /// it succeeded. The text lands in SERVERURLERROR, which the page shows in a dialog of its own.
    /// </remarks>
    [CustomAction]
    public static ActionResult ValidateServerUrl(Session session) => Run(session, () => ValidateUrl(session));

    private static ActionResult ValidateUrl(Session session)
    {
        var address = (session["RENDEZVOUSURL"] ?? string.Empty).Trim();
        // Written back trimmed: the configuration step reads this property, and a trailing space in a URL is the kind
        // of thing that only shows up as a binding failure later.
        session["RENDEZVOUSURL"] = address;

        var error = Space;
        if (address.Length == 0)
        {
            error = "请填写信令监听地址，例如 https://0.0.0.0:5080（只给本机用可以填 http://127.0.0.1:5080）。";
        }
        else if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
                 || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                 || uri.Host.Length == 0)
        {
            error = "信令监听地址必须是 http:// 或 https:// 开头的绝对地址，例如 https://0.0.0.0:5080。";
        }
        else if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "信令监听地址不能包含用户名或密码。";
        }
        else if (uri.Scheme == Uri.UriSchemeHttps
                 && string.Equals(session["TLSCHOICE"], "none", StringComparison.OrdinalIgnoreCase))
        {
            error = "地址是 https://，但上面没有启用 TLS：请选择「自动生成一张自签名证书」或「使用已有的证书文件」，"
                    + "或者把地址改成 http://（非回环的明文地址，客户端会拒绝连接）。";
        }

        session["SERVERURLERROR"] = error;
        return ActionResult.Success;
    }

    /// <summary>
    /// Puts the space back into boxes that were left empty, on the way out of a page.
    /// </summary>
    /// <remarks>
    /// Not cosmetic: a box the user clears has its property removed, and going back a page and returning rebuilds the
    /// control against a property that is no longer there — it comes out greyed, with no way left to type the value
    /// back in. The password is trimmed here too: that box arrives holding a single space (a property with no value
    /// never reaches the property table, and a control bound to one cannot be typed into), so anybody who clicks in and
    /// types without clearing it first ends up with a space in front of the password, and a .pfx opened with
    /// " secret" instead of "secret" simply fails to open. The cost is that a password that genuinely begins or ends
    /// with a space cannot be given here.
    ///
    /// This runs on the first two pages, and it deliberately leaves the database box alone: its default is the word
    /// "auto", which the page would then show in a box labelled "leave it empty for SQLite". <see
    /// cref="NormalizeSettings"/> handles that one where it belongs.
    /// </remarks>
    [CustomAction]
    public static ActionResult RestoreEmptyBoxes(Session session) => Run(session, () => Restore(session));

    /// <summary>
    /// The same tidy-up, plus the database box, on the way out of the last page.
    /// </summary>
    /// <remarks>
    /// The database box gets the word the package ships rather than a space, because empty has a meaning of its own
    /// there: "decide for this machine", which is a SQLite database. An empty connection string would reach the
    /// configuration step as "no database at all" — every device code lost on restart — so the word is what the step
    /// has to see, and the page says as much next to the box.
    /// </remarks>
    [CustomAction]
    public static ActionResult NormalizeSettings(Session session) => Run(session, () => Normalize(session));

    private static ActionResult Restore(Session session)
    {
        var password = (session["TLSCERTIFICATEPASSWORD"] ?? string.Empty).Trim();
        session["TLSCERTIFICATEPASSWORD"] = password.Length == 0 ? Space : password;

        foreach (var name in new[] { "TLSCERTIFICATEFILE", "RELAYPUBLICENDPOINT" })
        {
            if (string.IsNullOrWhiteSpace(session[name]))
            {
                session[name] = Space;
            }
        }

        return ActionResult.Success;
    }

    private static ActionResult Normalize(Session session)
    {
        _ = Restore(session);

        if (string.IsNullOrWhiteSpace(session["POSTGRESCONNECTIONSTRING"]))
        {
            session["POSTGRESCONNECTIONSTRING"] = "auto";
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
