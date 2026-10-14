using System.Net.Quic;
using System.Runtime.InteropServices;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Platform.Linux;

namespace RemoteDesktop.Client;

/// <summary>
/// <c>--self-check</c>: what this machine can and cannot do, in one command.
/// </summary>
/// <remarks>
/// <para>
/// The Linux report is in the platform layer and both hosts call it from here or from their own entry point —
/// see <see cref="LinuxSelfCheck.RunReport"/> for why it belongs there rather than beside either of them.
/// </para>
/// <para>
/// What is left on this side is the part that is about the operating system rather than about a platform layer,
/// which is what a Windows machine has instead.
/// </para>
/// </remarks>
internal static class SelfCheck
{
    /// <returns>Zero when everything required is present, otherwise one — so it can gate a script.</returns>
    public static int Run(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (OperatingSystem.IsLinux())
        {
            return LinuxSelfCheck.RunReport(output, DataDirectory.ForUser());
        }

        return RunElsewhere(output);
    }

    /// <summary>The same question on a machine with no platform layer of its own to ask.</summary>
    private static int RunElsewhere(TextWriter output)
    {
        var failures = new List<string>();

        void Write(TextWriter output, string name, bool ok, bool required, string detail)
        {
            output.WriteLine($"[{(ok ? " ok " : required ? "FAIL" : "warn")}] {name,-20} {detail}");
            if (!ok && required)
            {
                failures.Add(name);
            }
        }

        var directory = DataDirectory.ForUser();
        var writable = true;
        var why = directory;

        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".writable");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            writable = false;
            why = $"{directory} — {error.Message}";
        }

        output.WriteLine($"Remote Desktop self-check — {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture}), .NET {Environment.Version}");
        output.WriteLine();

        Write(output, "Data folder", writable, true, why);
        Write(
            output,
            "QUIC",
            QuicListener.IsSupported,
            true,
            QuicListener.IsSupported
                ? "System.Net.Quic is supported"
                : "no Schannel TLS 1.3 — Windows 10 needs 11 or Server 2022+, or an msquic.dll built against OpenSSL");

        output.WriteLine();
        if (failures.Count == 0)
        {
            output.WriteLine("All checks passed.");
            return 0;
        }

        output.WriteLine($"Failed: {string.Join(", ", failures)}");
        return 1;
    }
}
