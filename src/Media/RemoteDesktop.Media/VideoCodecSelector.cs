using RemoteDesktop.Abstractions.Media;

namespace RemoteDesktop.Media;

/// <summary>
/// Picks encoder/decoder backends: hardware first, software fallback. Platform projects register their factories.
/// </summary>
public sealed class VideoCodecSelector(IEnumerable<IVideoEncoderFactory> encoders, IEnumerable<IVideoDecoderFactory> decoders)
{
    private readonly List<IVideoEncoderFactory> _encoders = Order(encoders, f => f.IsHardware, f => f.Priority);
    private readonly List<IVideoDecoderFactory> _decoders = Order(decoders, f => f.IsHardware, f => f.Priority);

    /// <summary>Candidates in the order they should be tried, so a caller can fall back when one fails at runtime.</summary>
    public IEnumerable<IVideoEncoderFactory> GetEncoderCandidates(VideoEncoderOptions options) =>
        _encoders.Where(f => f.IsSupported(options));

    public IEnumerable<IVideoDecoderFactory> GetDecoderCandidates(VideoCodec codec) =>
        _decoders.Where(f => f.IsSupported(codec));

    /// <summary>Creates the first encoder that can be initialized.</summary>
    /// <param name="allowHardware"><c>false</c> after a hardware encoder failed at runtime, to fall back to software.</param>
    /// <exception cref="NotSupportedException">No registered backend supports <paramref name="options"/>.</exception>
    public IVideoEncoder CreateEncoder(VideoEncoderOptions options, bool allowHardware = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        return CreateFirst(
            GetEncoderCandidates(options).Where(f => allowHardware || !f.IsHardware),
            f => f.Create(options),
            $"No video encoder available for {options.Codec} {options.Width}x{options.Height}.");
    }

    /// <exception cref="NotSupportedException">No registered backend can decode <paramref name="codec"/>.</exception>
    public IVideoDecoder CreateDecoder(VideoCodec codec, int width, int height, bool allowHardware = true) =>
        CreateFirst(
            GetDecoderCandidates(codec).Where(f => allowHardware || !f.IsHardware),
            f => f.Create(codec, width, height),
            $"No video decoder available for {codec}.");

    private static TResult CreateFirst<TFactory, TResult>(IEnumerable<TFactory> candidates, Func<TFactory, TResult> create, string error)
    {
        List<Exception>? failures = null;
        foreach (var factory in candidates)
        {
            try
            {
                return create(factory);
            }
#pragma warning disable CA1031 // Any backend failure (driver, missing GPU) must fall through to the next backend.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                (failures ??= []).Add(ex);
            }
        }

        throw new NotSupportedException(error, failures is null ? null : new AggregateException(failures));
    }

    private static List<T> Order<T>(IEnumerable<T> items, Func<T, bool> isHardware, Func<T, int> priority)
    {
        ArgumentNullException.ThrowIfNull(items);
        return [.. items.OrderByDescending(isHardware).ThenByDescending(priority)];
    }
}
