using System.Diagnostics;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Media;
using RemoteDesktop.Platform.Linux.Video;

namespace RemoteDesktop.Platform.Linux.UnitTests;

/// <summary>
/// How an encoder gets chosen, against the machine this actually runs on.
/// </summary>
/// <remarks>
/// The interesting case is the one on this machine: a GPU whose driver offers no way to encode. The hardware factory
/// must notice and step aside rather than offering something that fails when a session starts, and the session must
/// then be carried by software without anything above having to know which happened.
/// </remarks>
public sealed class VideoCodecSelectionTests
{
    private const string LinuxOnly = "libavcodec and DRM devices, through the Linux platform layer.";

    private static VideoEncoderOptions Options => new(VideoCodec.H264, 640, 480, 30, 4000);

    [Fact]
    public void The_software_encoder_offers_itself_wherever_ffmpeg_is_installed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var software = new SoftwareH264EncoderFactory();

        Assert.False(software.IsHardware);
        Assert.True(software.IsSupported(Options));
    }

    [Fact]
    public void The_software_encoder_does_not_offer_h265()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // Nothing implements H.265 yet on either platform, and an encoder that claimed to would fail at the point a
        // controller asked for it rather than here.
        Assert.False(new SoftwareH264EncoderFactory().IsSupported(Options with { Codec = VideoCodec.H265 }));
    }

    [Fact]
    public void The_selection_falls_through_to_software_on_a_machine_that_cannot_encode_in_hardware()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var hardware = new VaapiH264EncoderFactory();
        var software = new SoftwareH264EncoderFactory();

        var selector = new VideoCodecSelector([hardware, software], []);

        // Whichever way this machine answers, a session must get an encoder — and it must be a working one.
        using var encoder = selector.CreateEncoder(Options);

        Assert.NotNull(encoder);
        Assert.True(
            hardware.IsSupported(Options) || !encoder.IsHardware,
            "A machine with no hardware encoder handed back one anyway.");
    }

    [Fact]
    public void Hardware_that_reports_itself_supported_can_actually_produce_something()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // The probe is only worth anything if it is honest: a factory that says yes must open an encoder and encode a
        // frame. This is the check that a probe reading the wrong thing would fail.
        var hardware = new VaapiH264EncoderFactory();
        if (!hardware.IsSupported(Options))
        {
            Assert.Skip("This machine has no VA-API encoding device; nothing to check.");
        }

        using var encoder = hardware.Create(Options);
        using var frame = new MemoryVideoFrame(640, 480, PixelFormat.Nv12, TimeSpan.Zero);
        frame.Buffer[..(640 * 480)].Fill(0x40);
        frame.Buffer[(640 * 480)..].Fill(0x80);

        Assert.NotEmpty(encoder.Encode(frame, forceKeyFrame: true));
    }

    [Fact]
    public void Probing_the_gpu_does_not_take_a_visible_amount_of_time()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // The probe runs once, on first use. If it were slow enough to see, it would be a stall at the start of the
        // first session rather than something nobody notices.
        var elapsed = Stopwatch.StartNew();
        _ = new VaapiH264EncoderFactory().IsSupported(Options);
        elapsed.Stop();

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"Probing the encoder took {elapsed.Elapsed}.");
    }

    [Fact]
    public void The_render_nodes_are_described_by_what_the_kernel_says_not_by_their_number()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        foreach (var node in DrmRenderNodes.Enumerate())
        {
            Assert.StartsWith("/dev/dri/renderD", node.Path, StringComparison.Ordinal);
            Assert.NotEmpty(node.Driver);

            // The vendor is read from sysfs. Zero means it could not be read, which is not an error — the capability
            // probe does not depend on knowing who made the card.
            if (node.Vendor != 0)
            {
                Assert.Contains(node.Vendor, new uint[] { 0x8086, 0x1002, 0x10de, 0x1af4, 0x15ad });
            }
        }
    }

    [Fact]
    public void Nvidia_render_nodes_are_recognised_as_not_va_api()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // VA-API on an NVIDIA card is decode only, so the probe must not spend time trying to encode there.
        var nvidia = new DrmRenderNode("/dev/dri/renderD128", 0x10de, "nvidia");
        var amd = new DrmRenderNode("/dev/dri/renderD128", 0x1002, "amdgpu");

        Assert.True(nvidia.IsNvidia);
        Assert.False(amd.IsNvidia);
    }
}
