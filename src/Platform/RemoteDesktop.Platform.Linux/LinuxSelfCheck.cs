using System.Net.Quic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Linux.Display;
using RemoteDesktop.Platform.Linux.Interop;
using RemoteDesktop.Platform.Linux.Video;
using RemoteDesktop.Platform.Linux.XdgPortal;

namespace RemoteDesktop.Platform.Linux;

/// <summary>
/// Asks this machine whether the pieces this platform needs are actually here.
/// </summary>
/// <remarks>
/// <para>
/// Written because of what goes wrong without it. Five separate native subsystems — PipeWire, libei, libavcodec,
/// the session D-Bus and the desktop portal — are reached through <c>DllImport</c> and through sockets, and the
/// ordinary failure is a <c>DllNotFoundException</c> with no context, a property read that never answers, or a
/// library that loads but is the wrong version. Each probe below answers one of those questions and says what it
/// saw, so that a machine which cannot share its screen says why in one command.
/// </para>
/// <para>
/// Nothing here asks for permission, opens a dialog, or starts a session: every probe is a read. That is what makes
/// it usable on a machine that is broken, which is the only machine worth running it on.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public static class LinuxSelfCheck
{
    /// <summary>
    /// One thing that either works here or does not.
    /// </summary>
    /// <param name="Name">What was asked, in the words an operator would use.</param>
    /// <param name="Ok">Whether the answer was what the product needs.</param>
    /// <param name="Required">Whether the product can do its job at all without it.</param>
    /// <param name="Detail">What was seen: a version, a path, or the reason it is not there.</param>
    public readonly record struct Probe(string Name, bool Ok, bool Required, string Detail);

    /// <summary>A 1080p stream at 4 Mbit/s, which is what the encoder factories are asked about.</summary>
    private static VideoEncoderOptions Sample => new(VideoCodec.H264, 1920, 1080, 30, 4000);

    public static IReadOnlyList<Probe> Run() =>
    [
        Session(),
        SessionBus(),
        RuntimeDirectory(),
        Portal(),
        DisplayLayout(),
        Native("PipeWire", PipeWire.Library, required: true,
            "Screen capture reads frames from it, and there is no other way in on Wayland."),
        Native("libei", LibEi.Library, required: true,
            "Input injection goes through it, and the alternative needs machine-wide privileges."),
        Ffmpeg(),
        Encoders(),
    ];

    /// <summary>
    /// Runs everything and writes the report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Here rather than in either host because <b>both</b> need it, and the one that needs it more is the
    /// controlled end: the portal, PipeWire, libei and the display are all things it reaches for, and a machine
    /// somebody is setting up as a controlled end may well have nothing else installed. A diagnostic that only
    /// ships with the controller is a diagnostic for the machine that was already working.
    /// </para>
    /// <para>
    /// The folder is passed in rather than worked out here: the two hosts keep their state in different
    /// directories on Windows, and only they know which one applies.
    /// </para>
    /// </remarks>
    /// <returns>Zero when everything required is present, otherwise one — so it can gate a script.</returns>
    public static int RunReport(TextWriter output, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(dataDirectory);

        List<Probe> probes = [DataFolder(dataDirectory), Quic()];
        try
        {
            probes.AddRange(Run());
        }
        catch (Exception error)
        {
            // The probes are ordinary code, and one of them may be the thing that is broken. Reporting that is
            // more use than a stack trace out of the diagnostic.
            probes.Add(new Probe("Linux platform", false, true, $"{error.GetType().Name}: {error.Message}"));
        }

        output.WriteLine($"Remote Desktop self-check — {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture}), .NET {Environment.Version}");
        output.WriteLine();

        foreach (var probe in probes)
        {
            output.WriteLine($"[{Mark(probe)}] {probe.Name,-20} {probe.Detail}");
        }

        var failed = probes.Where(probe => !probe.Ok && probe.Required).ToList();
        output.WriteLine();

        if (failed.Count == 0)
        {
            output.WriteLine($"All {probes.Count} checks passed.");
            return 0;
        }

        output.WriteLine($"{failed.Count} of {probes.Count} checks failed: "
            + string.Join(", ", failed.Select(probe => probe.Name)));
        return 1;
    }

    /// <summary>Where the product keeps its state, and whether it can be written to at all.</summary>
    private static Probe DataFolder(string directory)
    {
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
            "libmsquic.so.2 is missing — install libmsquic, or the .deb, which carries its own copy");

    private static string Mark(Probe probe) => probe.Ok ? " ok " : probe.Required ? "FAIL" : "warn";

    /// <summary>Which kind of desktop this is. The portal only answers inside a graphical session of the right kind.</summary>
    private static Probe Session()
    {
        var type = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        var wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");

        var detail = $"session '{Show(type)}' on '{Show(desktop)}'"
            + (string.IsNullOrEmpty(wayland) ? ", no WAYLAND_DISPLAY" : $", WAYLAND_DISPLAY={wayland}");

        // X11 is reported as a failure rather than a footnote: the portal path may well work there, but nothing on
        // this platform has been run against it, and saying so is the difference between an untested path and a
        // path believed to work.
        var ok = string.Equals(type, "wayland", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(wayland);
        return new Probe("Desktop session", ok, Required: true, detail);
    }

    /// <summary>The bus the portal is on. Without it there is nothing to ask, and no screen to share.</summary>
    private static Probe SessionBus()
    {
        var address = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        if (string.IsNullOrEmpty(address))
        {
            return new Probe("Session bus", false, true, "DBUS_SESSION_BUS_ADDRESS is not set");
        }

        // A unix socket that has been removed from underneath us fails much later with a connection refused that
        // reads like a portal problem, so it is worth saying now. D-Bus addresses can contain key-value options
        // separated by commas (such as ",guid=...").
        string? path = null;
        if (address.StartsWith("unix:path=", StringComparison.Ordinal))
        {
            var raw = address["unix:path=".Length..];
            var comma = raw.IndexOf(',');
            path = comma >= 0 ? raw[..comma] : raw;
        }

        if (path is not null && !File.Exists(path))
        {
            return new Probe("Session bus", false, true, $"{address} — but {path} is not there");
        }

        return new Probe("Session bus", true, true, address);
    }

    /// <summary>Where the host control socket lives. Needed by the controlled end, and writable only by this user.</summary>
    private static Probe RuntimeDirectory()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtime))
        {
            return new Probe("Runtime directory", false, true, "XDG_RUNTIME_DIR is not set");
        }

        return Directory.Exists(runtime)
            ? new Probe("Runtime directory", true, true, runtime)
            : new Probe("Runtime directory", false, true, $"{runtime} does not exist");
    }

    /// <summary>
    /// The portal itself, asked something it can answer without prompting anybody.
    /// </summary>
    /// <remarks>
    /// <c>IsCameraPresent</c> is a property: reading it raises no dialog, which is why it is the thing this pokes
    /// the portal with. That it doubles as the camera answer is a bonus rather than the point.
    /// <para>
    /// One name for every outcome, on purpose: this check is about the portal and the camera together — the camera
    /// question is asked through the portal because it is the one that can answer without a dialog — and a name that
    /// changed with the answer would look like a different check to the test that guards the list, and to anybody
    /// comparing two machines side by side.
    /// </para>
    /// </remarks>
    private static Probe Portal()
    {
        const string Name = "Portal and camera";

        try
        {
            using var connection = new PortalConnection();
            var camera = connection.CameraPresentAsync().GetAwaiter().GetResult();

            // A camera belongs here because its absence is not a fault and its presence is not a promise: this asks
            // the portal whether one exists, not whether the capture path works. That path has been verified on real
            // hardware (see WaylandCameras).
            return camera
                ? new Probe(Name, true, true, "answered; a camera is present")
                : new Probe(Name, true, true, "answered; no camera on this machine");
        }
        catch (Exception error) when (error is PortalException or InvalidOperationException)
        {
            return new Probe(Name, false, true, error.Message);
        }
        catch (Exception error) when (error is DllNotFoundException or TypeInitializationException)
        {
            return new Probe(Name, false, true, $"{error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>The display layout, which is also the answer to "can this program see a desktop at all".</summary>
    private static Probe DisplayLayout()
    {
        try
        {
            if (!MutterDisplayConfig.IsAvailable())
            {
                return new Probe("Display layout", false, true, "the compositor did not answer org.gnome.Mutter.DisplayConfig");
            }

            using var enumerator = new WaylandDisplayEnumerator(NullLogger<WaylandDisplayEnumerator>.Instance);
            var displays = enumerator.GetDisplays();

            return new Probe("Display layout", displays.Count > 0, true,
                string.Join("; ", displays.Select(d => $"{d.Id} {d.Bounds.Width}x{d.Bounds.Height}@{d.Bounds.X},{d.Bounds.Y}")));
        }
        catch (Exception error) when (error is InvalidOperationException or DllNotFoundException)
        {
            return new Probe("Display layout", false, true, $"{error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>Whether a native library the product loads by name is here at all.</summary>
    private static Probe Native(string name, string library, bool required, string why)
    {
        if (NativeLibrary.TryLoad(library, out var handle))
        {
            NativeLibrary.Free(handle);
            return new Probe(name, true, required, $"{library} loads");
        }

        return new Probe(name, false, required, $"{library} is not installed — {why}");
    }

    /// <summary>
    /// The codec library, and the version of it.
    /// </summary>
    /// <remarks>
    /// The version is the point rather than a detail: the bindings were generated for one major version and their
    /// entry points exist in that one only, so a distribution carrying a different libavcodec fails inside a codec
    /// with a missing symbol. Saying which one was found turns that into a sentence.
    /// </remarks>
    private static Probe Ffmpeg()
    {
        try
        {
            FfmpegLibraries.EnsureResolved();
            return new Probe("libavcodec", true, true, FfmpegLibraries.VersionString());
        }
        catch (Exception error) when (error is NotSupportedException or DllNotFoundException)
        {
            return new Probe("libavcodec", false, true, error.Message);
        }
        catch (Exception error) when (error is DllNotFoundException or TypeInitializationException)
        {
            return new Probe("libavcodec", false, true, $"{error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// Which H.264 encoders this machine can actually use.
    /// </summary>
    /// <remarks>
    /// Asked by opening one, not by looking for a name: every one of these encoder names is present in the FFmpeg
    /// build on a machine with no GPU at all — this one, for instance, has <c>h264_vaapi</c> and <c>h264_nvenc</c>
    /// and can use neither. The encoder-selection code already relies on this answer, so a machine that reports a
    /// hardware encoder missing here is the same machine the product will fall back to software on.
    /// </remarks>
    private static Probe Encoders()
    {
        var options = Sample;
        var usable = new List<string>();
        var unavailable = new List<string>();

        // In the order the product tries them, so the report reads the way the selection would go.
        IVideoEncoderFactory[] factories =
        [
            new NvencH264EncoderFactory(),
            new VaapiH264EncoderFactory(),
            new SoftwareH264EncoderFactory(),
        ];

        foreach (var factory in factories)
        {
            try
            {
                if (factory.IsSupported(options))
                {
                    usable.Add(factory.Name);
                }
                else
                {
                    unavailable.Add(factory.Name);
                }
            }
            catch (Exception error) when (error is NotSupportedException or DllNotFoundException or TypeInitializationException)
            {
                unavailable.Add($"{factory.Name} ({error.GetType().Name})");
            }
        }

        var detail = usable.Count == 0
            ? $"none usable; tried {string.Join(", ", unavailable)}"
            : $"usable: {string.Join(", ", usable)}"
              + (unavailable.Count == 0 ? string.Empty : $"; not available: {string.Join(", ", unavailable)}");

        return new Probe("H.264 encoders", usable.Count > 0, true, detail);
    }

    private static string Show(string? value) => string.IsNullOrEmpty(value) ? "unset" : value;
}
