using System.Net.Quic;
using System.Runtime.InteropServices;
using System.Text;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Platform.Linux;

namespace RemoteDesktop.Client;

/// <summary>
/// <c>--self-check</c>: what this machine can and cannot do, in one command.
/// </summary>
/// <remarks>
/// <para>
/// This is the entry point and nothing else: it parses the options, mirrors the report when one was asked for, and
/// dispatches to the report of the system it is running on. Where each report lives is a deliberate split:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Linux's is in the platform layer (<see cref="LinuxSelfCheck"/>), because <b>both</b> hosts need it — the portal,
/// PipeWire, libei and the display are all things the controlled end reaches for, and the machine being diagnosed
/// is often one with only the controlled end installed.
/// </description></item>
/// <item><description>
/// macOS's is next to this file (<c>SelfCheck.MacOS.cs</c>), because it walks the whole client stack as the client
/// builds it: the container, the media layer's codec ordering, and the bundle the process was launched from. The
/// platform layer cannot build that container — a platform project may not see the media layer — so a report that
/// mirrors the client has to live with the client.
/// </description></item>
/// <item><description>
/// Windows' is the short report at the bottom of this file. There is no platform-layer self-check on Windows
/// because the pieces it would ask about are the ones the installer and the service already verify.
/// </description></item>
/// </list>
/// <para>
/// The run happens before anything is built, so it still answers on a machine where the program cannot start — which
/// is the machine somebody runs it on. It writes to stdout and returns a process exit code, so it can also gate a
/// script.
/// </para>
/// <para>
/// <c>--output &lt;path&gt;</c> mirrors the whole report to a file as well. Together with the <c>RD_SELF_CHECK</c>
/// environment variable it is what makes a launch that discards its arguments or its stdout usable; the macOS
/// bundle is the concrete case and <c>Program.Main</c> carries the details.
/// </para>
/// </remarks>
internal static partial class SelfCheck
{
    /// <returns>Zero when everything required is present, otherwise one — so it can gate a script.</returns>
    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var outputPath = ReadOption(args, "--output");

        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // A redirected or closed stdout is not a reason to fail a diagnostic.
        }

        // Everything below writes through Console.Out. When a report file was asked for, the same text is mirrored
        // into a buffer as it is produced and written out at the end -- including when the run gives up part way
        // through -- because the interesting way to run this is a launch whose stdout goes nowhere.
        var stdout = Console.Out;
        var mirror = outputPath is null ? null : new StringBuilder();
        if (mirror is not null)
        {
            Console.SetOut(new MirrorWriter(stdout, mirror));
        }

        try
        {
            return Execute(args);
        }
        finally
        {
            Console.SetOut(stdout);
            WriteReport(outputPath, mirror, stdout);
        }
    }

    /// <summary>The diagnostic proper, with stdout already wired up by <see cref="Run"/>.</summary>
    private static int Execute(string[] args)
    {
        // The folder is resolved here rather than in each report: it is where the people who run this expect their
        // logs and identity to be, so it doubles as a check that the answer matches what the hosts use.
        var dataDirectory = ReadOption(args, "--DataDirectory") ?? DataDirectory.ForUser();

        if (OperatingSystem.IsMacOS())
        {
            return RunMacReport(dataDirectory);
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxSelfCheck.RunReport(Console.Out, dataDirectory);
        }

        return RunPlainReport(dataDirectory);
    }

    /// <summary>
    /// The same question on a machine with no platform report of its own to ask: the data folder and the transport,
    /// which are what a Windows machine has instead.
    /// </summary>
    private static int RunPlainReport(string dataDirectory)
    {
        var failures = new List<string>();

        void Write(string name, bool ok, bool required, string detail)
        {
            Console.WriteLine($"[{(ok ? " ok " : required ? "FAIL" : "warn")}] {name,-20} {detail}");
            if (!ok && required)
            {
                failures.Add(name);
            }
        }

        var writable = true;
        var why = dataDirectory;

        try
        {
            Directory.CreateDirectory(dataDirectory);
            var probe = Path.Combine(dataDirectory, ".writable");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            writable = false;
            why = $"{dataDirectory} — {error.Message}";
        }

        Console.WriteLine($"Remote Desktop self-check — {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture}), .NET {Environment.Version}");
        Console.WriteLine();

        Write("Data folder", writable, required: true, why);
        Write(
            "QUIC",
            QuicListener.IsSupported,
            required: true,
            QuicListener.IsSupported
                ? "System.Net.Quic is supported"
                : "no Schannel TLS 1.3 — Windows 10 needs 11 or Server 2022+, or an msquic.dll built against OpenSSL");

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("All checks passed.");
            return 0;
        }

        Console.WriteLine($"Failed: {string.Join(", ", failures)}");
        return 1;
    }

    /// <summary>Reads a <c>--name value</c> pair, or <see langword="null"/> when it is absent.</summary>
    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static void WriteReport(string? outputPath, StringBuilder? mirror, TextWriter stdout)
    {
        if (outputPath is null || mirror is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(outputPath, mirror.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            stdout.WriteLine($"{Environment.NewLine}   report written to {outputPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Failing to leave a report behind is not itself a reason to change the exit code.
            stdout.WriteLine($"{Environment.NewLine}   could not write the report to {outputPath}: {ex.Message}");
        }
    }

    /// <summary>Writes through to the real stdout and keeps a copy, so <c>--output</c> can replay it to a file.</summary>
    private sealed class MirrorWriter(TextWriter inner, StringBuilder mirror) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            inner.Write(value);
            mirror.Append(value);
        }

        public override void Write(string? value)
        {
            inner.Write(value);
            mirror.Append(value);
        }

        public override void WriteLine(string? value)
        {
            inner.WriteLine(value);
            mirror.AppendLine(value);
        }

        public override void Flush() => inner.Flush();
    }
}
