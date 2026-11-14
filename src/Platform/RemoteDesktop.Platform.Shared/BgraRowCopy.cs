namespace RemoteDesktop.Platform.Shared;

/// <summary>Copies BGRA rows out of a native buffer whose rows are padded wider than the visible picture.</summary>
/// <remarks>
/// ScreenCaptureKit and AVFoundation both hand over buffers whose bytes-per-row is a hardware alignment, usually wider
/// than <c>width * 4</c>. Both managed sinks had the same row-by-row loop; this is that loop, once. When the source
/// stride is narrower than the destination's, only the source stride's worth of bytes is copied — the same clamp the
/// two callers relied on.
/// </remarks>
internal static class BgraRowCopy
{
    /// <param name="source">The first byte of the source's first row.</param>
    /// <param name="sourceStride">Bytes from one source row to the next.</param>
    /// <param name="destination">A buffer of at least <paramref name="height"/> times <paramref name="destinationStride"/> bytes.</param>
    /// <param name="destinationStride">Bytes from one destination row to the next; normally <c>width * 4</c>.</param>
    /// <param name="height">Number of rows to copy.</param>
    public static unsafe void Copy(byte* source, int sourceStride, Span<byte> destination, int destinationStride, int height)
    {
        var rowBytes = Math.Min(sourceStride, destinationStride);
        for (var y = 0; y < height; y++)
        {
            new ReadOnlySpan<byte>(source + (y * sourceStride), rowBytes)
                .CopyTo(destination[(y * destinationStride)..]);
        }
    }
}
