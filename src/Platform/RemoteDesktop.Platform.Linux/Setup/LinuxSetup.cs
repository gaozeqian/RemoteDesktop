using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace RemoteDesktop.Platform.Linux.Setup;

/// <summary>
/// The few things the product changes about its own installation on Linux.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <c>SystemSetup</c> on Windows, and much smaller, because almost nothing here needs doing:
/// the controlled end is a <c>systemd --user</c> unit registered by the package rather than a service somebody
/// installs by hand, and there is no policy bit to set.
/// </para>
/// <para>
/// What is left is making the controlled end pick up a settings change. The address itself is written by
/// <c>UserSettings</c>, which is where both this side and macOS keep it.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public static class LinuxSetup
{
    /// <summary>
    /// The unit the package registers for the controlled end. The name is not ours to shorten: xdg-desktop-portal
    /// derives the app id from `app-<ApplicationID>.service`, and the desktop entry has to match it for the camera
    /// permission. This used to say `remotedesktop-host`, which no package has installed since the rename, so the
    /// restart after changing the address silently did nothing.
    /// </summary>
    public const string HostServiceUnit = "app-RemoteDesktopHost.service";

    /// <summary>
    /// Restarts the controlled end so it picks up a new address, if this machine is running one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It returns nothing because there is nothing a caller could do differently: the address is already saved to a
    /// file the service reads when it starts, so a machine with no unit to restart — somebody who installed only
    /// the client, or one that is not running it yet — simply takes the change on next start. Reporting a failure
    /// here would be reporting something with no consequence.
    /// </para>
    /// <para>
    /// The client and the controlled end need not even be on the same machine, which is the other reason this is
    /// housekeeping rather than part of saving.
    /// </para>
    /// </remarks>
    public static void RestartHostServiceIfInstalled()
    {
        try
        {
            var startInfo = new ProcessStartInfo("systemctl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            startInfo.ArgumentList.Add("--user");
            startInfo.ArgumentList.Add("restart");
            startInfo.ArgumentList.Add(HostServiceUnit);

            _ = RunToCompletion(startInfo, RestartTimeout);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            // No systemctl on this machine, or no systemd to talk to.
        }
    }

    /// <summary>How long the restart may take before it is abandoned; a unit that hangs must not hang the caller.</summary>
    private static readonly TimeSpan RestartTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs a process with both output streams redirected and waits for it, killing it if it outlives the timeout.
    /// </summary>
    /// <returns><see langword="true"/> if the process exited on its own in time; <see langword="false"/> if it could not be started or had to be killed.</returns>
    /// <remarks>
    /// Both streams are read concurrently: reading one to the end before starting on the other blocks the child as
    /// soon as it fills the pipe the reader is not looking at, and then neither side ever finishes.
    /// </remarks>
    internal static bool RunToCompletion(ProcessStartInfo startInfo, TimeSpan timeout)
    {
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        var drained = Task.WhenAll(
            process.StandardOutput.ReadToEndAsync(),
            process.StandardError.ReadToEndAsync());

        if (process.WaitForExit(timeout))
        {
            // The streams reach end-of-file once the process (and anything that inherited its pipes) is gone; the
            // wait is bounded for the same reason the process wait is.
            _ = drained.Wait(timeout);
            return true;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            // It exited between the timeout and the kill.
        }

        return false;
    }
}
