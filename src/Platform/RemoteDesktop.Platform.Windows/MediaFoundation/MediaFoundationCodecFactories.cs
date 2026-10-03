using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Media;

namespace RemoteDesktop.Platform.Windows.MediaFoundation;

/// <summary>
/// H.264 encoders registered with Media Foundation. One instance enumerates hardware transforms (NVENC, Quick Sync,
/// AMF), the other the software ones (Microsoft H.264 encoder), giving hardware first with a built-in fallback.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationH264EncoderFactory : IVideoEncoderFactory
{
    private readonly uint _flags;
    private readonly Lazy<List<MftDescriptor>> _transforms;

    public MediaFoundationH264EncoderFactory(bool hardware)
    {
        IsHardware = hardware;
        _flags = (hardware ? MfGuids.EnumHardware : MfGuids.EnumSyncMft) | MfGuids.EnumSortAndFilter;
        _transforms = new(() => SafeEnumerate(() =>
            MfRuntime.Enumerate(MfGuids.CategoryVideoEncoder, _flags, MfGuids.VideoFormatNv12, MfGuids.VideoFormatH264)));
    }

    public string Name => IsHardware ? "Media Foundation hardware H.264" : "Media Foundation software H.264";

    public bool IsHardware { get; }

    public int Priority => 0;

    /// <summary>Friendly names of the transforms found, for diagnostics.</summary>
    public IReadOnlyList<string> TransformNames => [.. _transforms.Value.Select(t => t.Name)];

    public bool IsSupported(VideoEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Codec == VideoCodec.H264 && _transforms.Value.Count > 0;
    }

    public IVideoEncoder Create(VideoEncoderOptions options)
    {
        List<Exception>? failures = null;
        foreach (var transform in _transforms.Value)
        {
            try
            {
                return new MfH264Encoder(transform, _flags, IsHardware, options);
            }
#pragma warning disable CA1031 // A driver refusing the configuration must not hide the next transform.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                (failures ??= []).Add(ex);
            }
        }

        throw new NotSupportedException($"{Name}: no transform accepted {options.Width}x{options.Height}.",
            failures is null ? null : new AggregateException(failures));
    }

    internal static List<MftDescriptor> SafeEnumerate(Func<List<MftDescriptor>> enumerate)
    {
        try
        {
            return enumerate();
        }
#pragma warning disable CA1031 // Media Foundation may be missing (Windows N editions, Server Core).
        catch (Exception)
#pragma warning restore CA1031
        {
            return [];
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class MediaFoundationH264DecoderFactory : IVideoDecoderFactory
{
    private const uint Flags = MfGuids.EnumSyncMft | MfGuids.EnumSortAndFilter;

    private readonly Lazy<List<MftDescriptor>> _transforms = new(() => MediaFoundationH264EncoderFactory.SafeEnumerate(() =>
        MfRuntime.Enumerate(MfGuids.CategoryVideoDecoder, Flags, MfGuids.VideoFormatH264, MfGuids.VideoFormatNv12)));

    public string Name => "Media Foundation H.264 decoder";

    public bool IsHardware => false;

    public int Priority => 0;

    public bool IsSupported(VideoCodec codec) => codec == VideoCodec.H264 && _transforms.Value.Count > 0;

    public IVideoDecoder Create(VideoCodec codec, int width, int height)
    {
        List<Exception>? failures = null;
        foreach (var transform in _transforms.Value)
        {
            try
            {
                return new MfH264Decoder(transform, Flags, width, height);
            }
#pragma warning disable CA1031 // Try the next registered decoder.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                (failures ??= []).Add(ex);
            }
        }

        throw new NotSupportedException($"{Name}: no transform could be created.", failures is null ? null : new AggregateException(failures));
    }
}
