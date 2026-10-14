using System.Net.Quic;
using System.Runtime.InteropServices;
using RemoteDesktop.Application.Hosting;

namespace RemoteDesktop.Client;

/// <summary>
/// <c>--self-check</c>: what this machine can and cannot do, in one command.
/// </summary>
/// <remarks>
/// <para>
/// The product reaches five native subsystems through <c>DllImport</c> and two more through sockets, and the usual
/// way that goes wrong is a <c>DllNotFoundException</c> with no context or a call that never comes back. The
/// alternative to this is a walk through <c>ldconfig</c>, <c>busctl</c> and the journal to work out which of them
/// is missing, on the machine that is already broken.
/// </para>
/// <para>
/// Runs before anything else the program does, so it works on a machine where the program would not otherwise
/// start, and asks nothing that would raise a dialog — a diagnostic that needs permission is no use on a machine
/// whose permission is what is being tested.
/// </para>
/// </remarks>
internal static class SelfCheck
{
    /// <summary>One question and its answer.</summary>
    /// <param name="Required">Whether the product has any use without it — a missing camera is not a fault.</param>
    private readonly record struct Probe(string Name, bool Ok, bool Required, string Detail);

    /// <returns>Zero when everything required is present, otherwise one — so it can gate a script.</returns>
    public static int Run(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var probes = new List<Probe>
        {
            DataFolder(),
            Quic(),
        };

        if (OperatingSystem.IsLinux())
        {
            try
            {
                foreach (var probe in Platform.Linux.LinuxSelfCheck.Run())
                {
                    probes.Add(new Probe(probe.Name, probe.Ok, probe.Required, probe.Detail));
                }
            }
            catch (Exception error)
            {
                // The probes are ordinary code and one of them may be the thing that is broken. Reporting that is
                // more use than a stack trace from the diagnostic.
                probes.Add(new Probe(
                    "Linux platform",
                    false,
                    true,
                    $"{error.GetType().Name}: {error.Message}"));
            }
        }

        output.WriteLine($"Remote Desktop self-check — {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture}), .NET {Environment.Version}");
        output.WriteLine();

        foreach (var probe in probes)
        {
            output.WriteLine($"[{Mark(probe)}] {probe.Name,-20} {probe.Detail}");
        }

        var failed = probes.Count(p => !p.Ok && p.Required);
        output.WriteLine();

        if (failed == 0)
        {
            output.WriteLine($"All {probes.Count} checks passed.");
            return 0;
        }

        output.WriteLine($"{failed} of {probes.Count} checks failed: "
            + string.Join(", ", probes.Where(p => !p.Ok && p.Required).Select(p => p.Name)));
        return 1;
    }

    /// <summary>Where the product keeps its state, and whether it can be written to at all.</summary>
    private static Probe DataFolder()
    {
        var directory = DataDirectory.ForUser();

        try
        {
            Directory.CreateDirectory(directory);

            // Written rather than looked at: a folder that exists but belongs to somebody else fails later, when
            // the identity is being saved, and reads as a problem with the identity rather than with the folder.
            var probe = Path.Combine(directory, ".writable");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);

            return new Probe("Data folder", true, true, directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new Probe("Data folder", false, true, $"{directory} — {error.Message}");
        }
    }

    /// <summary>
    /// Whether this machine can speak QUIC, which every connection needs.
    /// </summary>
    /// <remarks>
    /// The failure this catches is quiet: <c>System.Net.Quic</c> is a managed wrapper over a native library that
    /// Windows gets from the runtime and Linux does not, and without it every connection fails with "QUIC is not
    /// supported on this system" — a sentence that reads like a network problem and is not one.
    /// </remarks>
    private static Probe Quic() => QuicListener.IsSupported
        ? new Probe("QUIC", true, true, "System.Net.Quic is supported")
        : new Probe(
            "QUIC",
            false,
            true,
            OperatingSystem.IsWindows()
                ? "no Schannel TLS 1.3 — Windows 10 needs 11 or Server 2022+, or an msquic.dll built against OpenSSL"
                : "libmsquic.so.2 is missing — install libmsquic, or the .deb, which carries its own copy");

    private static string Mark(Probe probe) => probe.Ok ? " ok " : probe.Required ? "FAIL" : "warn";
}
