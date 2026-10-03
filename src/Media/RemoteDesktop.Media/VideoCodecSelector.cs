using RemoteDesktop.Abstractions.Media;

namespace RemoteDesktop.Media;

/// <summary>
/// Picks encoder/decoder backends: hardware first, software fallback. Platform projects register their hardware
/// factories; this project registers the cross-platform software ones.
/// </summary>
public sealed class VideoCodecSelector(IEnumerable<IVideoEncoderFactory> encoders, IEnumerable<IVideoDecoderFactory> decoders)
{
    private readonly IReadOnlyList<IVideoEncoderFactory> _encoders = Order(encoders, f => f.IsHardware, f => f.Priority);
    private readonly IReadOnlyList<IVideoDecoderFactory> _decoders = Order(decoders, f => f.IsHardware, f => f.Priority);

    /// <summary>Candidates in the order they should be tried, so a caller can fall back when creation fails at runtime.</summary>
    public IEnumerable<IVideoEncoderFactory> GetEncoderCandidates(VideoEncoderOptions options) =>
        _encoders.Where(f => f.IsSupported(options));

    public IEnumerable<IVideoDecoderFactory> GetDecoderCandidates(VideoCodec codec) =>
        _decoders.Where(f => f.IsSupported(codec));

    /// <summary>Creates the first encoder that can be initialized.</summary>
    /// <exception cref="NotSupportedException">No registered backend supports <paramref name="options"/>.</exception>
    public IVideoEncoder CreateEncoder(VideoEncoderOptions options)
    {
        List<Exception>? failures = null;
        foreach (var factory in GetEncoderCandidates(options))
        {
            try
            {
                return factory.Create(options);
            }
#pragma warning disable CA1031 // Any backend failure (driver, missing GPU) must fall through to the next backend.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                (failures ??= []).Add(ex);
            }
        }

        throw new NotSupportedException(
            $"No video encoder available for {options.Codec} {options.Width}x{options.Height}.",
            failures is null ? null : new AggregateException(failures));
    }

    private static List<T> Order<T>(IEnumerable<T> items, Func<T, bool> isHardware, Func<T, int> priority)
    {
        ArgumentNullException.ThrowIfNull(items);
        return [.. items.OrderByDescending(isHardware).ThenByDescending(priority)];
    }
}
