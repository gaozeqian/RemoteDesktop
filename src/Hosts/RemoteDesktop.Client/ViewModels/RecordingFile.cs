using System.Globalization;

namespace RemoteDesktop.Client.ViewModels;

/// <summary>
/// Picks the file a new recording is written to.
/// </summary>
/// <remarks>
/// The name is only as precise as the second, and two windows — or two recordings started back to back — can share
/// one. Both would then hand the same path to a fresh writer, which recreates the file the user was just told was
/// saved, so an existing name is stepped over rather than reused.
/// </remarks>
internal static class RecordingFile
{
    public static string NextPath(string folder, string prefix)
    {
        // The recordings folder does not exist on a fresh install, and a recorder handed a path inside a missing
        // directory fails with an error that names neither the folder nor the reason. Creating it here means the
        // first recording works, and the same call site covers the screen and the camera.
        Directory.CreateDirectory(folder);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, $"{prefix}_{stamp}.mp4");

        for (var suffix = 2; File.Exists(path); suffix++)
        {
            path = Path.Combine(folder, $"{prefix}_{stamp}_{suffix}.mp4");
        }

        return path;
    }
}
