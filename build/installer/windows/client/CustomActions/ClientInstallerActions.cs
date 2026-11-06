using WixToolset.Dtf.WindowsInstaller;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace RemoteDesktop.Client.Installer.CustomActions;

/// <summary>
/// The parts of the client/host installation that a command line or a WiX control cannot do: move the live settings out
/// of the MSI-managed folder on the first upgrade to the new layout, validate/prefill the server connection page, ask
/// for a certificate file, pin the server certificate, and hand the fixed password to the service without putting it in
/// the host's command line.
/// </summary>
public static class ClientInstallerActions
{
    private const string DataDirectoryName = "RemoteDesktop";
    private const string CertificateDirectoryName = "certificates";
    private const string CertificateFileName = "signaling.cer";
    private const string PasswordFileName = ".fixed-password";
    private const string Space = " ";
    private const string ServiceRegistryPath = @"SYSTEM\CurrentControlSet\Services\RemoteDesktopHost";

    /// <summary>
    /// Before RemoveExistingProducts removes the old product's files, copy the settings it kept under Program Files to
    /// %ProgramData%\RemoteDesktop if the destination is not there yet. Every later upgrade finds it and does nothing.
    /// </summary>
    [CustomAction]
    public static ActionResult MigrateClientSettings(Session session) => Run(session, "MigrateClientSettings", () =>
    {
        // INSTALLFOLDER is a directory property and is not visible to a deferred action; SetClientInstallDir copies it
        // into this secure property in the immediate pass.
        var installDirectory = session["CLIENTINSTALLFOLDER"];
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            throw new InvalidOperationException("CLIENTINSTALLFOLDER is not set.");
        }

        var source = Path.Combine(installDirectory, "appsettings.json");
        var destinationDirectory = DataDirectory();
        var destination = Path.Combine(destinationDirectory, "appsettings.json");
        if (!File.Exists(source) || File.Exists(destination))
        {
            session.Log($"Client settings migration skipped: source '{source}' exists={File.Exists(source)}; " +
                        $"destination '{destination}' exists={File.Exists(destination)}.");
            return ActionResult.Success;
        }

        Directory.CreateDirectory(destinationDirectory);
        File.Copy(source, destination);
        session.Log($"Migrated {source} to {destination}.");
        return ActionResult.Success;
    });

    /// <summary>
    /// Brings the connection page up with the values this machine is already using when this is an upgrade. A fresh
    /// installation keeps the build/command-line defaults; an upgrade keeps the live address and certificate pin unless
    /// the operator changes them, which is what makes the page safe to show on every install.
    /// </summary>
    [CustomAction]
    public static ActionResult PrefillConnectionSettings(Session session) => Run(session, "PrefillConnectionSettings", () =>
    {
        // Going Back to the folder page and Next again must not replace what the operator typed on this page.
        if (session["CONNECTIONEDITED"] == "1")
        {
            return ActionResult.Success;
        }

        if (string.IsNullOrEmpty(session["Installed"]) && !HostServiceExists())
        {
            return ActionResult.Success;
        }

        // ProgramData is the current layout. The first upgrade from the layout that kept the file under Program Files
        // finds nothing there yet (the migration in the execute sequence has not run while the UI is up), so read the
        // old file too; otherwise the page would show the build default and an operator pressing Next would overwrite
        // the address the migration is about to preserve.
        var settings = Path.Combine(DataDirectory(), "appsettings.json");
        if (!File.Exists(settings))
        {
            var installDirectory = session["INSTALLFOLDER"];
            if (!string.IsNullOrWhiteSpace(installDirectory))
            {
                settings = Path.Combine(installDirectory, "appsettings.json");
            }
        }

        if (!File.Exists(settings))
        {
            return ActionResult.Success;
        }

        string json;
        try
        {
            json = File.ReadAllText(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            session.Log($"Could not read {settings}; the connection page keeps the package defaults: {ex.Message}");
            return ActionResult.Success;
        }

        var liveAddress = ReadJsonString(json, "RendezvousUri");
        var currentAddress = session["RENDEZVOUSURI"];
        var packageAddress = session["DEFAULT_RENDEZVOUSURI"];
        if (!string.IsNullOrWhiteSpace(liveAddress)
            && (string.IsNullOrWhiteSpace(currentAddress)
                || string.Equals(currentAddress.Trim(), packageAddress?.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            session["RENDEZVOUSURI"] = liveAddress;
            session.Log($"Prefilled the server address from {settings}.");
        }

        if (string.IsNullOrWhiteSpace(session["SERVERCERTIFICATEPATH"])
            && AddressScheme(liveAddress) == "wss")
        {
            var pin = ReadJsonString(json, "RendezvousCertificatePath");
            if (!string.IsNullOrWhiteSpace(pin) && File.Exists(pin))
            {
                session["SERVERCERTIFICATEPATH"] = pin;
                session.Log($"Prefilled the pinned server certificate from {pin}.");
            }
        }

        return ActionResult.Success;
    });

    /// <summary>
    /// Checks the connection page before it leaves: a ws:// address may not carry a certificate, and a certificate for
    /// a wss:// address has to be an absolute path to a readable PEM/DER certificate. It does not copy anything; the
    /// execute sequence's <see cref="PrepareRendezvousCertificate"/> does that where even a silent installation runs.
    /// </summary>
    [CustomAction]
    public static ActionResult ValidateConnectionSettings(Session session) => Run(session, "ValidateConnectionSettings", () =>
    {
        // The execute sequence only applies the connection values on an upgrade when this page actually ran.
        session["CONNECTIONEDITED"] = "1";

        var address = (session["RENDEZVOUSURI"] ?? string.Empty).Trim();
        session["RENDEZVOUSURI"] = address;
        var certificate = (session["SERVERCERTIFICATEPATH"] ?? string.Empty).Trim();
        session["SERVERCERTIFICATEPATH"] = certificate.Length == 0 ? Space : certificate;

        var error = Space;
        if (address.Length == 0)
        {
            error = "请填写服务器地址，例如 wss://rd.example.com:5080/ws。";
        }
        else if (!TryValidateAddress(address, out var addressError))
        {
            error = addressError;
        }
        else if (AddressScheme(address) == "ws" && certificate.Length > 0)
        {
            error = "服务器地址是 ws://，不需要证书。请清空证书，或把地址改成 wss://。";
        }
        else if (AddressScheme(address) == "wss" && certificate.Length > 0
                 && !TryLoadCertificate(certificate, out var loaded, out var certificateError))
        {
            error = certificateError;
            loaded?.Dispose();
        }

        session["CONNECTIONERROR"] = error;
        return ActionResult.Success;
    });

    /// <summary>Asks for the server certificate's public half and puts the path where the page shows it.</summary>
    [CustomAction]
    public static ActionResult BrowseForServerCertificate(Session session) => Run(session, "BrowseForServerCertificate", () =>
    {
        var current = session["SERVERCERTIFICATEPATH"];
        if (string.IsNullOrWhiteSpace(current))
        {
            current = string.Empty;
        }

        var file = new string('\0', 1024);
        var dialog = new OpenFileName
        {
            Size = Marshal.SizeOf<OpenFileName>(),
            Owner = GetForegroundWindow(),
            Filter = "服务器证书 (*.cer;*.crt;*.pem;*.der)\0*.cer;*.crt;*.pem;*.der\0所有文件 (*.*)\0*.*\0\0",
            FilterIndex = 1,
            File = file,
            MaxFile = file.Length,
            Title = "选择服务器证书（公钥，PEM 或 DER）",
            Flags = OfnExplorer | OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir,
        };

        if (current.Length > 0)
        {
            dialog.InitialDirectory = Path.GetDirectoryName(current);
            dialog.File = Path.GetFileName(current) + new string('\0', 1024 - Path.GetFileName(current).Length);
        }

        if (GetOpenFileNameW(ref dialog))
        {
            session["SERVERCERTIFICATEPATH"] = dialog.File.Split('\0')[0];
            session["SERVERCERTIFICATECHOSEN"] = "1";
        }
        else
        {
            session["SERVERCERTIFICATECHOSEN"] = "0";
        }

        return ActionResult.Success;
    });

    /// <summary>
    /// Exports the public half of the chosen certificate to <c>%ProgramData%\RemoteDesktop\certificates</c> and hands
    /// the path to the deferred configure step. The source file is never copied wholesale: a PEM or PFX may contain a
    /// private key, and the pin only needs the certificate. No certificate, or a ws:// address, removes an old pin.
    /// </summary>
    [CustomAction]
    public static ActionResult PrepareRendezvousCertificate(Session session) => Run(session, "PrepareRendezvousCertificate", () =>
    {
        var address = (session["RENDEZVOUSURI"] ?? string.Empty).Trim();
        if (!TryValidateAddress(address, out var addressError))
        {
            session.Log($"The server address is invalid: {addressError}");
            return ActionResult.Failure;
        }

        var certificate = (session["SERVERCERTIFICATEPATH"] ?? string.Empty).Trim();
        var pin = CertificatePath();
        if (AddressScheme(address) == "wss" && certificate.Length > 0)
        {
            if (!TryLoadCertificate(certificate, out var loaded, out var certificateError))
            {
                session.Log(certificateError);
                return ActionResult.Failure;
            }

            using (loaded)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(pin)!);
                var temporary = pin + ".tmp";
                File.WriteAllBytes(temporary, loaded!.Export(X509ContentType.Cert));
                File.Delete(pin); // File.Move has no overwrite on .NET Framework
                File.Move(temporary, pin);
            }

            session["RENDEZVOUSCERTIFICATEPATH"] = pin;
            session.Log($"Pinned the public half of {certificate} as {pin}.");
        }
        else
        {
            if (File.Exists(pin))
            {
                File.Delete(pin);
                session.Log($"Removed the old certificate pin {pin}.");
            }

            session["RENDEZVOUSCERTIFICATEPATH"] = Space;
            if (certificate.Length > 0)
            {
                session.Log("The server address is not wss://; the certificate was not imported.");
            }
        }

        return ActionResult.Success;
    });

    /// <summary>
    /// Writes the fixed password to a file only SYSTEM and Administrators can read, and tells the deferred action its
    /// path. The service has supported <c>--set-fixed-password-file</c> since the Linux package needed it; using it
    /// here keeps the secret out of the host process's command line, which every local user can read.
    /// </summary>
    [CustomAction]
    public static ActionResult WriteFixedPasswordFile(Session session) => Run(session, "WriteFixedPasswordFile", () =>
    {
        session["FIXEDPASSWORDFILE"] = string.Empty;

        var password = session["FIXEDPASSWORD"];
        if (string.IsNullOrWhiteSpace(password))
        {
            return ActionResult.Success;
        }

        var directory = DataDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, PasswordFileName);

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sidType in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sidType, domainSid: null),
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(password);
        }

        new FileInfo(path).SetAccessControl(security);
        session["FIXEDPASSWORDFILE"] = path;
        return ActionResult.Success;
    });

    /// <summary>Removes the temporary password file after the service has read it.</summary>
    /// <remarks>
    /// The path is computed rather than read from FIXEDPASSWORDFILE: a deferred managed action cannot read ordinary
    /// session properties (Windows Installer only gives it CustomActionData), and the file is always at the one path
    /// <see cref="WriteFixedPasswordFile"/> wrote it to.
    /// </remarks>
    [CustomAction]
    public static ActionResult DeleteFixedPasswordFile(Session session) => Run(session, "DeleteFixedPasswordFile", () =>
    {
        var path = Path.Combine(DataDirectory(), PasswordFileName);
        if (File.Exists(path))
        {
            File.Delete(path);
            session.Log($"Deleted {path}.");
        }

        return ActionResult.Success;
    });

    private static string DataDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        DataDirectoryName);

    private static string CertificatePath() => Path.Combine(
        DataDirectory(),
        CertificateDirectoryName,
        CertificateFileName);

    private static bool HostServiceExists()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceRegistryPath);
            return key is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    /// <summary>Reads one string from the installer's own settings JSON without taking a JSON dependency.</summary>
    private static string? ReadJsonString(string json, string name)
    {
        var match = Regex.Match(
            json,
            "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"\\\\])*)\"");
        if (!match.Success)
        {
            return null;
        }

        try
        {
            return Regex.Unescape(match.Groups["value"].Value);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string AddressScheme(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Scheme : string.Empty;

    private static bool TryValidateAddress(string address, out string error)
    {
        error = string.Empty;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || (uri.Scheme != "ws" && uri.Scheme != "wss")
            || string.IsNullOrEmpty(uri.Host))
        {
            error = "服务器地址必须是 ws:// 或 wss:// 开头的绝对地址，例如 wss://rd.example.com:5080/ws。";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "服务器地址不能包含用户名或密码。";
            return false;
        }

        // Same host rule as RendezvousAddress: a DNS name or an IP literal. The brackets and colons of an IPv6 host
        // are the only exception; whether the host is an IPv6 literal is what Uri tells us, not whether it contains a
        // colon (a colon in a DNS name is the thing this refuses).
        if (uri.HostNameType != UriHostNameType.IPv6)
        {
            foreach (var character in uri.Host)
            {
                if (!(char.IsLetterOrDigit(character) || character is '.' or '-'))
                {
                    error = "服务器地址的主机名包含不支持的字符。";
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Loads a PEM or DER certificate. The caller exports only <see cref="X509ContentType.Cert"/> from it, so a source
    /// that also contains a private key never puts that key on the machine.
    /// </summary>
    private static bool TryLoadCertificate(
        string path,
        out X509Certificate2? certificate,
        out string error)
    {
        certificate = null;
        error = string.Empty;
        if (!Path.IsPathRooted(path))
        {
            error = $"证书路径必须是绝对路径：{path}";
            return false;
        }

        if (!File.Exists(path))
        {
            error = $"证书文件不存在：{path}";
            return false;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            var text = Encoding.UTF8.GetString(bytes);
            const string beginMarker = "-----BEGIN CERTIFICATE-----";
            const string endMarker = "-----END CERTIFICATE-----";
            var begin = text.IndexOf(beginMarker, StringComparison.Ordinal);
            var end = text.IndexOf(endMarker, StringComparison.Ordinal);
            var der = begin >= 0 && end > begin
                ? Convert.FromBase64String(Regex.Replace(
                    text.Substring(begin + beginMarker.Length, end - begin - beginMarker.Length),
                    @"\s+",
                    string.Empty))
                : bytes;
            certificate = new X509Certificate2(der);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or FormatException)
        {
            error = $"证书无法读取或不是 PEM/DER 证书：{path}";
            return false;
        }
    }

    private static ActionResult Run(Session session, string name, Func<ActionResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            session.Log($"{name} failed: {ex}");
            return ActionResult.Failure;
        }
    }

    private const int OfnExplorer = 0x00080000;
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnPathMustExist = 0x00000800;
    private const int OfnNoChangeDir = 0x00000008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int Size;
        public IntPtr Owner;
        public IntPtr Instance;
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
        public IntPtr CustomData;
        public IntPtr Hook;
        public string TemplateName;
        public IntPtr Reserved;
        public int Reserved2;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OpenFileName dialog);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
