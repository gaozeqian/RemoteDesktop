using System.Runtime.Versioning;
using FFmpeg.AutoGen;

namespace RemoteDesktop.Platform.Linux.Interop;

/// <summary>
/// Finds the FFmpeg shared libraries and hands their directory to the bindings, which load by absolute path and
/// therefore cannot find anything on their own.
/// </summary>
/// <remarks>
/// <para>
/// The bindings were generated for one major version and their entry points only exist in that one — they ask the
/// loader for <c>libavcodec.so.62</c> and nothing else. So this searches for that exact file rather than for
/// "some libavcodec": finding a different one would load, resolve nothing, and fail much later with an error about a
/// missing symbol from somewhere inside a codec.
/// </para>
/// <para>
/// The distribution's own libraries are preferred over anything bundled, because those are the ones that get security
/// updates. A path can be forced with <c>RD_FFMPEG_PATH</c> for a machine that keeps them elsewhere.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal static class FfmpegLibraries
{
    /// <summary>Overrides where the libraries are looked for.</summary>
    public const string PathVariable = "RD_FFMPEG_PATH";

    private static readonly string[] SearchPaths =
    [
        // The usual place on a Debian-derived system, and where a self-contained build would put its own copy.
        "/usr/lib/x86_64-linux-gnu",
        "/usr/lib/aarch64-linux-gnu",
        "/usr/lib64",
        "/usr/lib",
        "/usr/local/lib",
    ];

    private static readonly Lock Gate = new();
    private static bool _resolved;

    /// <summary>The major version of libavcodec the bindings were generated against.</summary>
    public static int RequiredMajorVersion => ffmpeg.LIBAVCODEC_VERSION_MAJOR;

    /// <summary>
    /// Points the bindings at the directory holding the libraries. Safe to call more than once.
    /// </summary>
    /// <exception cref="NotSupportedException">The right version is not installed; the message says which.</exception>
    public static void EnsureResolved()
    {
        lock (Gate)
        {
            if (_resolved)
            {
                return;
            }

            var directory = Environment.GetEnvironmentVariable(PathVariable);
            if (string.IsNullOrEmpty(directory))
            {
                directory = SearchPaths.FirstOrDefault(HasRequiredLibraries);
            }

            if (directory is null)
            {
                throw new NotSupportedException(
                    $"FFmpeg {RequiredMajorVersion} was not found. This build needs libavcodec.so.{RequiredMajorVersion} " +
                    $"(the libavcodec{RequiredMajorVersion} package on Ubuntu), or a directory holding it in {PathVariable}.");
            }

            if (!HasRequiredLibraries(directory))
            {
                throw new NotSupportedException(
                    $"{PathVariable} points at '{directory}', which does not hold libavcodec.so.{RequiredMajorVersion}.");
            }

            ffmpeg.RootPath = directory;
            _resolved = true;
        }
    }

    /// <summary>The version string of the libraries that were loaded, for diagnostics.</summary>
    public static string VersionString()
    {
        EnsureResolved();
        return ffmpeg.av_version_info();
    }

    private static bool HasRequiredLibraries(string directory) =>
        File.Exists(Path.Combine(directory, $"libavcodec.so.{RequiredMajorVersion}"))
        && File.Exists(Path.Combine(directory, $"libavutil.so.{ffmpeg.LIBAVUTIL_VERSION_MAJOR}"));
}
