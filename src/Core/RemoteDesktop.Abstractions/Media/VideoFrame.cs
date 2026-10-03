using RemoteDesktop.Abstractions.Display;

namespace RemoteDesktop.Abstractions.Media;

public enum PixelFormat
{
    Bgra32,
    Nv12,
    I420,
}

/// <summary>
/// A raw frame. The pixel memory is owned by the producer and is only valid until <see cref="Dispose()"/> is called,
/// so consumers must finish with it (or copy it) before disposing.
/// </summary>
public abstract class VideoFrame : IDisposable
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required PixelFormat Format { get; init; }

    public required TimeSpan Timestamp { get; init; }

    /// <summary>Regions that changed since the previous frame; empty means the whole frame.</summary>
    public IReadOnlyList<PixelRect> DirtyRects { get; init; } = [];

    /// <summary>Native GPU texture handle (e.g. ID3D11Texture2D*) when the frame lives on the GPU, otherwise zero.</summary>
    public virtual nint NativeTexture => 0;

    /// <summary>CPU-accessible pixels. May trigger a GPU readback for texture-backed frames.</summary>
    public abstract ReadOnlySpan<byte> GetPixels(out int stride);

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}
