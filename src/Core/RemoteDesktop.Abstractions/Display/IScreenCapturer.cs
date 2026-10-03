using RemoteDesktop.Abstractions.Media;

namespace RemoteDesktop.Abstractions.Display;

public interface IScreenCapturerFactory
{
    IScreenCapturer Create(DisplayInfo display);
}

/// <summary>Captures one display. Implementations may hand out GPU textures; see <see cref="VideoFrame"/>.</summary>
public interface IScreenCapturer : IAsyncDisposable
{
    DisplayInfo Display { get; }

    /// <summary>
    /// Waits for the next changed frame. Returns <c>null</c> when nothing changed within <paramref name="timeout"/>,
    /// which lets the pipeline lower the frame rate for static content.
    /// </summary>
    ValueTask<VideoFrame?> CaptureAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Raised when the cursor shape changes, so it can be rendered locally on the controller.</summary>
    event EventHandler<CursorShape>? CursorChanged;
}

public sealed record CursorShape(int Width, int Height, int HotspotX, int HotspotY, ReadOnlyMemory<byte> Bgra);
