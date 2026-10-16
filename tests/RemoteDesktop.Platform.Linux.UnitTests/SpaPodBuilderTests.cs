using RemoteDesktop.Platform.Linux.Display;

namespace RemoteDesktop.Platform.Linux.UnitTests;

/// <summary>
/// Writing the pods that ask for a picture format.
/// </summary>
/// <remarks>
/// The bytes here are checked against output from the real builder rather than against a second copy of the same
/// understanding. That distinction is not theoretical: the parser next to this once disagreed with PipeWire by four
/// bytes and its tests agreed with it, because both had been written from the same wrong idea. A format request is
/// the one thing in the capture path that can be wrong without anything failing.
/// </remarks>
public sealed class SpaPodBuilderTests
{
    private const string LinuxOnly = "The SPA pod format, which only PipeWire uses.";

    /// <summary>
    /// What <c>spa_pod_builder_add_object</c> produces for a BGRx request at 1920x1080.
    /// </summary>
    /// <remarks>
    /// Dumped from a program compiled against <c>spa/pod/builder.h</c>. Every word here has a job:
    /// <c>38</c> is the body's length, <c>0f</c> is an object, <c>03 00 04 00</c> is
    /// <c>SPA_TYPE_OBJECT_Format</c> (262147), <c>03</c> is <c>SPA_PARAM_EnumFormat</c>, <c>01 00 02 00</c> is
    /// <c>SPA_FORMAT_VIDEO_format</c> (131073), <c>08</c> is BGRx, <c>03 00 02 00</c> is
    /// <c>SPA_FORMAT_VIDEO_size</c> (131075), and the last two words are the rectangle.
    /// </remarks>
    private static readonly byte[] CursorlessBgrxRequest =
    [
        0x38, 0x00, 0x00, 0x00, 0x0f, 0x00, 0x00, 0x00, 0x03, 0x00, 0x04, 0x00, 0x03, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00,
        0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x08, 0x00, 0x00, 0x00, 0x0a, 0x00, 0x00, 0x00, 0x80, 0x07, 0x00, 0x00, 0x38, 0x04, 0x00, 0x00,
    ];

    [Fact]
    public void A_bgrx_request_comes_out_byte_for_byte_as_pipewire_builds_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        Assert.Equal(CursorlessBgrxRequest, SpaPodBuilder.EnumFormatBgrx(1920, 1080));
    }

    [Fact]
    public void The_request_reads_back_as_the_format_that_was_asked_for()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var pod = SpaPod.Parse(SpaPodBuilder.EnumFormatBgrx(2560, 1440));

        Assert.Equal(SpaPodType.Object, pod.Type);
        Assert.Equal(SpaPodBuilder.TypeObjectFormat, pod.AsObjectType());
        Assert.Equal(SpaPodBuilder.ParamEnumFormat, pod.AsObjectId());

        var properties = pod.AsProperties();
        Assert.Equal(2, properties.Count);

        Assert.Equal(SpaPodBuilder.FormatVideoFormat, properties[0].Key);
        Assert.Equal(SpaPodBuilder.VideoBgrx, properties[0].Value.AsId());

        Assert.Equal(SpaPodBuilder.FormatVideoSize, properties[1].Key);
        Assert.Equal(new SpaRectangle(2560, 1440), properties[1].Value.AsRectangle());
    }

    [Fact]
    public void A_pod_carries_its_real_length_and_pads_separately()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // An id body is four bytes and the pod occupies eight, so the length word counts four while the next pod
        // starts eight bytes along. Writing the padded length instead gives a pod that reads back correctly on its
        // own and shifts everything after it by four bytes.
        var request = SpaPodBuilder.EnumFormatBgrx(1920, 1080);
        var pod = SpaPod.Parse(request);

        Assert.Equal(56, pod.Size);
        Assert.Equal(64, request.Length);

        var idPod = pod.AsProperties()[0].Value;
        Assert.Equal(4, idPod.Size);
    }

    [Fact]
    public void Asking_for_a_cursor_describes_it_separately_from_the_picture()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var pod = SpaPod.Parse(SpaPodBuilder.CursorMeta(1024 * 1024));

        Assert.Equal(SpaPodBuilder.TypeObjectFormat, pod.AsObjectType());
        Assert.Equal(SpaPodBuilder.PipeWireParamMeta, pod.AsObjectId());

        var properties = pod.AsProperties();
        Assert.Equal(2, properties.Count);

        Assert.Equal(SpaPodBuilder.PipeWireMetaType, properties[0].Key);
        Assert.Equal(SpaPodBuilder.PipeWireMetaCursor, properties[0].Value.AsId());
        Assert.Equal(SpaPodBuilder.PipeWireMetaSize, properties[1].Key);
        Assert.Equal(1024 * 1024, properties[1].Value.AsInt());
    }

    [Fact]
    public void A_size_that_could_not_be_a_screen_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        Assert.Throws<ArgumentOutOfRangeException>(() => SpaPodBuilder.EnumFormatBgrx(0, 1080));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpaPodBuilder.EnumFormatBgrx(1920, 0));
    }

    /// <summary>What <c>spa_pod_builder_add_object</c> produces for a YUY2 request at 1280x720, 30 fps.</summary>
    /// <remarks>
    /// Read out of a program compiled against the headers, the same way the BGRx vector above was. The parts worth
    /// naming: <c>80</c> is the body's length, <c>01</c> is <c>SPA_FORMAT_mediaType</c> holding
    /// <c>SPA_MEDIA_TYPE_video</c> (2), <c>02</c> is <c>SPA_FORMAT_mediaSubtype</c> holding
    /// <c>SPA_MEDIA_SUBTYPE_raw</c> (1), then <c>SPA_FORMAT_VIDEO_format</c> holding <c>SPA_VIDEO_FORMAT_YUY2</c>
    /// (4) — and the last two words are <c>30/1</c> as a fraction.
    /// </remarks>
    private static readonly byte[] YuyvRequest =
    [
        0x80, 0x00, 0x00, 0x00, 0x0f, 0x00, 0x00, 0x00, 0x03, 0x00, 0x04, 0x00,
        0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x04, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x04, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x04, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x08, 0x00, 0x00, 0x00, 0x0a, 0x00, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00,
        0xd0, 0x02, 0x00, 0x00, 0x04, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x08, 0x00, 0x00, 0x00, 0x0b, 0x00, 0x00, 0x00, 0x1e, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00,
    ];

    /// <summary>The same request for JPEG, which names no pixel format — the encoding is the format.</summary>
    private static readonly byte[] JpegRequest =
    [
        0x68, 0x00, 0x00, 0x00, 0x0f, 0x00, 0x00, 0x00, 0x03, 0x00, 0x04, 0x00,
        0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x04, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x04, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x03, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x08, 0x00, 0x00, 0x00, 0x0a, 0x00, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00,
        0xd0, 0x02, 0x00, 0x00, 0x04, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x08, 0x00, 0x00, 0x00, 0x0b, 0x00, 0x00, 0x00, 0x1e, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00,
    ];

    [Fact]
    public void A_yuyv_request_comes_out_byte_for_byte_as_pipewire_builds_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        Assert.Equal(YuyvRequest, SpaPodBuilder.EnumFormatYuyv(1280, 720, 30));
    }

    [Fact]
    public void A_jpeg_request_comes_out_byte_for_byte_as_pipewire_builds_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        Assert.Equal(JpegRequest, SpaPodBuilder.EnumFormatJpeg(1280, 720, 30));
    }

    [Fact]
    public void A_yuyv_request_reads_back_as_the_format_that_was_asked_for()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var properties = SpaPod.Parse(SpaPodBuilder.EnumFormatYuyv(1280, 720, 30)).AsProperties();

        Assert.Equal(5, properties.Count);
        Assert.Equal(SpaPodBuilder.FormatMediaType, properties[0].Key);
        Assert.Equal(SpaPodBuilder.MediaTypeVideo, properties[0].Value.AsId());
        Assert.Equal(SpaPodBuilder.FormatMediaSubtype, properties[1].Key);
        Assert.Equal(SpaPodBuilder.MediaSubtypeRaw, properties[1].Value.AsId());
        Assert.Equal(SpaPodBuilder.FormatVideoFormat, properties[2].Key);
        Assert.Equal(SpaPodBuilder.VideoYuy2, properties[2].Value.AsId());
        Assert.Equal(SpaPodBuilder.FormatVideoSize, properties[3].Key);
        Assert.Equal(new SpaRectangle(1280, 720), properties[3].Value.AsRectangle());
        Assert.Equal(SpaPodBuilder.FormatVideoFramerate, properties[4].Key);
    }

    [Fact]
    public void A_jpeg_request_says_jpeg_and_names_no_pixel_format()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var properties = SpaPod.Parse(SpaPodBuilder.EnumFormatJpeg(640, 480, 15)).AsProperties();

        Assert.Equal(4, properties.Count);
        Assert.Equal(SpaPodBuilder.FormatMediaSubtype, properties[1].Key);
        Assert.Equal(SpaPodBuilder.MediaSubtypeJpeg, properties[1].Value.AsId());

        // Nothing claims to be a pixel format, because nothing in this request is one.
        Assert.DoesNotContain(properties, property => property.Key == SpaPodBuilder.FormatVideoFormat);
        Assert.Equal(new SpaRectangle(640, 480), properties[2].Value.AsRectangle());
    }

    [Fact]
    public void A_camera_size_that_could_not_be_one_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        Assert.Throws<ArgumentOutOfRangeException>(() => SpaPodBuilder.EnumFormatYuyv(0, 720, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpaPodBuilder.EnumFormatJpeg(1280, 0, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpaPodBuilder.EnumFormatJpeg(1280, 720, 0));
    }
}
