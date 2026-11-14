using System.Runtime.Versioning;
using FFmpeg.AutoGen;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Linux.Interop;

namespace RemoteDesktop.Platform.Linux.Video;

/// <summary>
/// The software H.264 encoder, which is the one that always works: no GPU, no driver and no session required.
/// </summary>
/// <remarks>
/// It is registered last on purpose. A machine with a GPU that can encode should use it — encoding a screen on the
/// CPU is the single largest cost this product has — but the software encoder is what makes the product work at all
/// on a server, in a virtual machine, or on a card whose driver there is no way to reach.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class SoftwareH264EncoderFactory : IVideoEncoderFactory
{
    public string Name => "libavcodec software H.264 (x264)";

    public bool IsHardware => false;

    public int Priority => 0;

    /// <summary>
    /// Whether this build of FFmpeg has an H.264 encoder at all, answered once and remembered.
    /// </summary>
    /// <remarks>
    /// On Debian and Ubuntu the encoders that were once covered by patents live in a separate package, and a plain
    /// install has no libx264 to find. Worth establishing once, because the alternative is a machine that silently
    /// encodes nothing and looks like a network problem.
    /// </remarks>
    private static readonly Lazy<bool> X264Available = new(() =>
    {
        FfmpegLibraries.EnsureResolved();
        unsafe
        {
            return ffmpeg.avcodec_find_encoder_by_name("libx264") is not null;
        }
    });

    public bool IsSupported(VideoEncoderOptions options) =>
        options.Codec == VideoCodec.H264 && X264Available.Value;

    public IVideoEncoder Create(VideoEncoderOptions options) => new FfmpegH264Encoder("libx264", options);
}

/// <summary>
/// Hardware H.264 through VA-API, which is how Intel and AMD GPUs are reached on Linux.
/// </summary>
/// <remarks>
/// <para>
/// Whether a render node can encode is established by trying it, not by asking what it supports. The distinction
/// matters because libva's answer is shaped by constants from a header that is not installed on every machine, and a
/// probe that read the wrong one would be wrong in the direction that silently never uses the GPU. Trying to open
/// the encoder is the question the host actually cares about, and it cannot be wrong about it.
/// </para>
/// <para>
/// The cost is one encoder opened and closed per node, once per process, which is a few milliseconds. The result is
/// remembered, including a negative one — a node that cannot encode will not start being able to during a session.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class VaapiH264EncoderFactory : IVideoEncoderFactory
{
    private readonly Lazy<IReadOnlyList<string>> _nodes = new(ProbeRenderNodes);

    public string Name => "libavcodec VA-API H.264";

    public bool IsHardware => true;

    public int Priority => 10;

    public bool IsSupported(VideoEncoderOptions options) =>
        options.Codec == VideoCodec.H264 && _nodes.Value.Count > 0;

    public IVideoEncoder Create(VideoEncoderOptions options)
    {
        var failures = new List<Exception>();
        foreach (var node in _nodes.Value)
        {
            try
            {
                return new FfmpegH264Encoder("h264_vaapi", options, hardware: true, device: node, uploadToDevice: true);
            }
#pragma warning disable CA1031 // One node refusing the settings must not stop the next one being tried.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failures.Add(ex);
            }
        }

        throw new NotSupportedException(
            $"No encoding device accepted {options.Width}x{options.Height} at {options.BitrateKbps} kbps.",
            failures.Count > 0 ? new AggregateException(failures) : null);
    }

    /// <summary>The render nodes that actually encode, established by opening the encoder on each.</summary>
    private static List<string> ProbeRenderNodes()
    {
        FfmpegLibraries.EnsureResolved();

        // Small and cheap: the question is whether the device can encode at all, and a device that can encode a small
        // picture can encode a large one. The size is deliberately not one of the sizes the session will use, so that
        // a probe cannot be mistaken for real work in a log.
        var probe = new VideoEncoderOptions(VideoCodec.H264, 320, 240, 30, 500);

        var working = new List<string>();
        foreach (var node in DrmRenderNodes.Enumerate())
        {
            // NVIDIA's render node cannot do VA-API encoding at all, so there is nothing to find out by trying.
            if (node.IsNvidia)
            {
                continue;
            }

            try
            {
                using var encoder = new FfmpegH264Encoder("h264_vaapi", probe, hardware: true, device: node.Path, uploadToDevice: true);
                working.Add(node.Path);
            }
#pragma warning disable CA1031 // A node that cannot encode is the expected answer on most machines.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        return working;
    }
}

/// <summary>
/// Hardware H.264 through NVENC.
/// </summary>
/// <remarks>
/// Unlike VA-API this need not guess at the card: <c>h264_nvenc</c> only exists in an FFmpeg build that has NVIDIA's
/// encoder compiled in, and it opens a CUDA device itself. So the probe is whether the encoder is there at all, and
/// whether it accepts the settings is left to <see cref="Create"/> — which is what the selector's fallback is for.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class NvencH264EncoderFactory : IVideoEncoderFactory
{
    private readonly Lazy<bool> _available = new(() =>
    {
        FfmpegLibraries.EnsureResolved();
        unsafe
        {
            // No render node of NVIDIA's is looked at: NVENC is reached through CUDA, not through /dev/dri, and a
            // machine whose driver exposes no render node (a container, a headless box) can still encode perfectly
            // well. Whether the settings can actually be opened is left to Create and the selector's fallback.
            return ffmpeg.avcodec_find_encoder_by_name("h264_nvenc") is not null;
        }
    });

    public string Name => "libavcodec NVENC H.264";

    public bool IsHardware => true;

    public int Priority => 20;

    public bool IsSupported(VideoEncoderOptions options) =>
        options.Codec == VideoCodec.H264 && _available.Value;

    /// <remarks>NVENC takes the frame as it is, so nothing is uploaded to a device and no render node is named.</remarks>
    public IVideoEncoder Create(VideoEncoderOptions options) =>
        new FfmpegH264Encoder("h264_nvenc", options, hardware: true);
}

/// <summary>The H.264 decoder, which needs no hardware and no device.</summary>
[SupportedOSPlatform("linux")]
internal sealed class FfmpegVideoDecoderFactory : IVideoDecoderFactory
{
    private readonly Lazy<bool> _available = new(() =>
    {
        FfmpegLibraries.EnsureResolved();
        unsafe
        {
            return ffmpeg.avcodec_find_decoder_by_name("h264") is not null;
        }
    });

    public string Name => "libavcodec H.264";

    public bool IsHardware => false;

    public int Priority => 0;

    public bool IsSupported(VideoCodec codec) => codec == VideoCodec.H264 && _available.Value;

    public IVideoDecoder Create(VideoCodec codec, int width, int height) =>
        codec == VideoCodec.H264
            ? new FfmpegVideoDecoder("h264", width, height)
            : throw new NotSupportedException($"This decoder does not do {codec}.");
}
