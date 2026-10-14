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
        // reads like a portal problem, so it is worth saying now.
        var path = address.StartsWith("unix:path=", StringComparison.Ordinal) ? address["unix:path=".Length..] : null;
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
    /// </remarks>
    private static Probe Portal()
    {
        try
        {
            using var connection = new PortalConnection();
            var camera = connection.CameraPresentAsync().GetAwaiter().GetResult();

            // A camera belongs here because its absence is not a fault and its presence is not a promise: the path
            // that would use one has never been run against real hardware.
            return camera
                ? new Probe("Desktop portal", true, true, "answered; a camera is present (the camera path is unverified)")
                : new Probe("Portal and camera", true, true, "answered; no camera on this machine");
        }
        catch (Exception error) when (error is PortalException or InvalidOperationException)
        {
            return new Probe("Desktop portal", false, true, error.Message);
        }
        catch (Exception error) when (error is DllNotFoundException or TypeInitializationException)
        {
            return new Probe("Desktop portal", false, true, $"{error.GetType().Name}: {error.Message}");
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
