using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Camera;

namespace RemoteDesktop.Platform.Linux.Camera;

/// <summary>
/// Cameras as the desktop portal knows them — which, on this machine, is none.
/// </summary>
/// <remarks>
/// <para>
/// The portal exposes whether there is a camera at all as a property, <c>IsCameraPresent</c>, which this machine
/// answers <c>false</c> to. Reading it is a property read rather than a prompt, so it is safe to do here; opening
/// one is a question for a person, and asking somebody to grant access to a camera this machine does not have would
/// be a dialog for an empty list.
/// </para>
/// <para>
/// So the honest state is: no cameras, because there are none. A machine that has one would still be told there are
/// none, because enumerating them properly is P8 — the portal hands over a descriptor with the device already open,
/// and the frames then arrive over V4L2 ioctls on that descriptor, decoded from MJPEG or YUYV. None of that is
/// written yet, and this class says so rather than leaving the operator to wonder where their camera went.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class WaylandCameraEnumerator(ILogger<WaylandCameraEnumerator> logger) : ICameraEnumerator
{
    private bool _reported;

    /// <inheritdoc />
    public IReadOnlyList<CameraInfo> GetCameras()
    {
        if (!_reported)
        {
            _reported = true;
            LogNoneYet(logger);
        }

        return [];
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Cameras are not enumerated on this platform yet, so none are offered. See docs/linux-port-plan.md (P8).")]
    private static partial void LogNoneYet(ILogger logger);
}

/// <summary>Opens a camera. There is nothing to open, because none are ever listed.</summary>
[SupportedOSPlatform("linux")]
public sealed class WaylandCameraSourceFactory : ICameraSourceFactory
{
    /// <exception cref="NotSupportedException">Always. See <see cref="WaylandCameraEnumerator"/>.</exception>
    public ICameraSource Open(string cameraId) =>
        throw new NotSupportedException("Cameras are not supported on this platform yet (P8).");
}
