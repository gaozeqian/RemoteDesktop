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

public interface ICameraSource : IAsyncDisposable
{
    CameraInfo Camera { get; }

    IAsyncEnumerable<VideoFrame> CaptureAsync(CameraMode mode, CancellationToken cancellationToken);
}
