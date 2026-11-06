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
    /// Whether the capturer knows it cannot deliver pictures any more: a lost device, a duplication that will not be
    /// recreated, a dead capture loop. <c>null</c> frames alone say nothing — a screen nobody is touching produces
    /// none either — so only the backend can tell "nothing changed" from "broken", and callers must not guess.
    /// </summary>
    bool IsFaulted => false;

    /// <summary>
    /// Throws away whatever the backend holds and starts over on the next capture. Some failures cannot be retried
    /// on the same object (a removed graphics device, for instance), and a capture object left alone after one is
    /// what turns a glitch into a picture that never moves again until the process restarts. A no-op where the
    /// backend has nothing to rebuild.
    /// </summary>
    void Reset()
    {
    }

    /// <summary>
    /// Waits for the next changed frame. Returns <c>null</c> when nothing changed within <paramref name="timeout"/>,
    /// which lets the pipeline lower the frame rate for static content.
    /// </summary>
    ValueTask<VideoFrame?> CaptureAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Raised when the cursor shape changes, so the controller can draw the real pointer itself.</summary>
    event EventHandler<CursorShape>? CursorChanged;

    /// <summary>Raised when the cursor moves or is shown / hidden. Both cursor events fire from inside <see cref="CaptureAsync"/>.</summary>
    event EventHandler<CursorPosition>? CursorMoved;
}

/// <param name="Bgra">Straight-alpha pixels, <paramref name="Width"/> × <paramref name="Height"/> × 4 bytes.</param>
public sealed record CursorShape(int Width, int Height, int HotspotX, int HotspotY, ReadOnlyMemory<byte> Bgra);

/// <param name="X">Left edge of the cursor image in pixels of the captured display.</param>
/// <param name="Y">Top edge of the cursor image in pixels of the captured display.</param>
public readonly record struct CursorPosition(int X, int Y, bool Visible);
