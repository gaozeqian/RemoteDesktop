using RemoteDesktop.Abstractions.Media;

namespace RemoteDesktop.Abstractions.Camera;

public sealed record CameraInfo(string Id, string Name, IReadOnlyList<CameraMode> Modes);

public sealed record CameraMode(int Width, int Height, int FrameRate);

public interface ICameraEnumerator
{
    IReadOnlyList<CameraInfo> GetCameras();
}

public interface ICameraSourceFactory
{
    ICameraSource Open(string cameraId);
}

/// <summary>
/// One camera's frames.
/// </summary>
/// <remarks>
/// Disposal is final: it cancels every capture the source is running and a later <see cref="CaptureAsync"/> throws
/// <see cref="ObjectDisposedException"/>. The device itself is opened and released inside each enumeration, so a
/// consumer that ends the enumeration has already released it; <see cref="IAsyncDisposable.DisposeAsync"/> only has
/// to stop the enumerations that are still in flight.
/// </remarks>
public interface ICameraSource : IAsyncDisposable
{
    CameraInfo Camera { get; }

    IAsyncEnumerable<VideoFrame> CaptureAsync(CameraMode mode, CancellationToken cancellationToken);
}
