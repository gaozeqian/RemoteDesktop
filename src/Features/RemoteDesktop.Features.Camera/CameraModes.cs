using RemoteDesktop.Abstractions.Camera;

namespace RemoteDesktop.Features.Camera;

/// <summary>Which of a camera's modes the host streams from.</summary>
public static class CameraModes
{
    /// <summary>The most frames per second that are ever sent to the controller.</summary>
    public const int MaxFrameRate = 30;

    /// <summary>
    /// The modes worth choosing from: the ones at or below <see cref="MaxFrameRate"/>, or, when the camera offers
    /// none of those (60/120 fps cameras, capture cards), every mode at the lowest rate it has — those are streamed
    /// with frames dropped down to <see cref="MaxFrameRate"/>. A camera that offers a mode at or below the limit never
    /// has a faster mode picked, whatever its picture size.
    /// </summary>
    public static IReadOnlyList<CameraMode> Usable(IReadOnlyList<CameraMode> modes)
    {
        ArgumentNullException.ThrowIfNull(modes);
        var slow = modes.Where(m => m.FrameRate <= MaxFrameRate).ToArray();
        if (slow.Length > 0 || modes.Count == 0)
        {
            return slow;
        }

        var lowest = modes.Min(m => m.FrameRate);
        return [.. modes.Where(m => m.FrameRate == lowest)];
    }
}
