using System.Buffers.Binary;

namespace RemoteDesktop.Platform.Linux.Display;

/// <summary>
/// Writes the pods that ask PipeWire for a format.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="SpaPod"/>, and the reason it has to exist at all: the C header builds these with
/// <c>spa_pod_builder_add_object</c>, which is a macro over a set of static inline functions, so there is no symbol
/// to call. The format is written out by hand instead.
/// </para>
/// <para>
/// Hand-written means hand-checked. The bytes this produces for a BGRx request at a given size are compared, in the
/// tests, against the bytes the real builder produces for the same request — dumped once from a program compiled
/// against <c>spa/pod/builder.h</c>. Nothing else about this format is trustworthy enough to be its own reference.
/// </para>
/// <para>
/// The rules, all of them load-bearing:
/// <list type="bullet">
/// <item>Every pod is <c>{uint32 size; uint32 type}</c> followed by its body. <c>size</c> counts the <em>unpadded</em>
/// body, but the pod occupies its body rounded up to eight bytes.</item>
/// <item>An object's body is <c>{uint32 object_type; uint32 object_id}</c> and then its properties. There is no
/// count.</item>
/// <item>A property is <c>{uint32 key; uint32 flags}</c> and then a whole pod, which starts at the next eight-byte
/// boundary.</item>
/// </list>
/// </para>
/// </remarks>
internal static class SpaPodBuilder
{
    /// <summary>SPA_TYPE_OBJECT_Format.</summary>
    public const uint TypeObjectFormat = 262147;

    /// <summary>SPA_PARAM_EnumFormat — what the stream would accept.</summary>
    public const uint ParamEnumFormat = 3;

    /// <summary>SPA_FORMAT_VIDEO_format.</summary>
    public const uint FormatVideoFormat = 131073;

    /// <summary>SPA_FORMAT_VIDEO_size.</summary>
    public const uint FormatVideoSize = 131075;

    /// <summary>SPA_VIDEO_FORMAT_BGRx.</summary>
    public const uint VideoBgrx = 8;

    /// <summary>
    /// One screen-sized BGRx frame: what a screen-cast stream is asked for.
    /// </summary>
    /// <remarks>
    /// The size is pinned rather than offered as a range on purpose. The compositor has already told us the
    /// rectangle it is sharing, so anything else would either be refused or leave the capture path scaling a frame
    /// it did not need to scale.
    /// </remarks>
    public static byte[] EnumFormatBgrx(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var properties = new List<byte>();
        properties.AddRange(Property(FormatVideoFormat, Id(VideoBgrx)));
        properties.AddRange(Property(FormatVideoSize, Rectangle(width, height)));

        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes(TypeObjectFormat));
        body.AddRange(BitConverter.GetBytes(ParamEnumFormat));
        body.AddRange(properties);

        return Pod(SpaPodType.Object, [.. body]);
    }

    /// <summary>SPA_FORMAT_mediaType.</summary>
    public const uint FormatMediaType = 1;

    /// <summary>SPA_FORMAT_mediaSubtype.</summary>
    public const uint FormatMediaSubtype = 2;

    /// <summary>SPA_FORMAT_VIDEO_framerate.</summary>
    public const uint FormatVideoFramerate = 131076;

    /// <summary>SPA_MEDIA_TYPE_video.</summary>
    public const uint MediaTypeVideo = 2;

    /// <summary>SPA_MEDIA_SUBTYPE_raw — uncompressed pixels.</summary>
    public const uint MediaSubtypeRaw = 1;

    /// <summary>SPA_MEDIA_SUBTYPE_jpeg — a still picture per frame.</summary>
    public const uint MediaSubtypeJpeg = 196609;

    /// <summary>
    /// SPA_MEDIA_SUBTYPE_mjpg — the subtype a V4L2 camera actually advertises for motion JPEG.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="MediaSubtypeJpeg"/>, and that distinction is not cosmetic: the v4l2 source on this
    /// machine enumerates its compressed capture formats as <c>Spa:Enum:MediaSubtype:mjpg</c> (Id 131074), so a
    /// request that only names <c>jpeg</c> is refused with "no more input formats" even though the camera produces
    /// motion JPEG perfectly well. Both are decoded by the same libavcodec MJPEG decoder.
    /// </remarks>
    public const uint MediaSubtypeMjpg = 131074;

    /// <summary>SPA_VIDEO_FORMAT_YUY2 — 4:2:2 with the chroma interleaved between the luminance samples.</summary>
    public const uint VideoYuy2 = 4;

    /// <summary>
    /// What a camera is asked for instead of raw pixels: one JPEG per frame.
    /// </summary>
    /// <remarks>
    /// Worth asking for first at any size a webcam is likely to run at. A 1280x720 frame is 1.8 MB as raw 4:2:2 and
    /// a hundred kilobytes as JPEG, and the copy out of the stream buffer and the trip through the decoder are both
    /// paid on every frame of a live preview.
    /// </remarks>
    public static byte[] EnumFormatJpeg(int width, int height, int frameRate) =>
        CameraFormat(width, height, frameRate, MediaSubtypeJpeg);

    /// <summary>
    /// What a V4L2 camera is asked for: motion JPEG under the subtype its source advertises.
    /// </summary>
    /// <remarks>
    /// A camera does not have to be a V4L2 node under the hood — the portal can hand over a camera that speaks
    /// <c>jpeg</c> instead — so both requests are sent. This one first, because it is the form the hardware path
    /// actually offers.
    /// </remarks>
    public static byte[] EnumFormatMjpg(int width, int height, int frameRate) =>
        CameraFormat(width, height, frameRate, MediaSubtypeMjpg);

    /// <summary>
    /// What a camera is asked for when it will not give JPEG: 4:2:2 as the device produces it.
    /// </summary>
    /// <remarks>
    /// YUY2 rather than something already planar because this asks for what a camera is likely to have natively —
    /// almost every one is 4:2:2 or 4:2:0 out of the sensor, and asking for a format the device does not produce
    /// leaves PipeWire inserting a converter we would then have to hope is configured the way ours is.
    /// </remarks>
    public static byte[] EnumFormatYuyv(int width, int height, int frameRate) =>
        CameraFormat(width, height, frameRate, MediaSubtypeRaw, VideoYuy2);

    /// <summary>
    /// One camera format. Compressed formats differ only by the subtype; raw ones also name their pixel format.
    /// </summary>
    private static byte[] CameraFormat(int width, int height, int frameRate, uint subtype, uint? pixelFormat = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(frameRate, 1);

        var properties = new List<byte>();
        properties.AddRange(Property(FormatMediaType, Id(MediaTypeVideo)));
        properties.AddRange(Property(FormatMediaSubtype, Id(subtype)));
        if (pixelFormat is { } format)
        {
            properties.AddRange(Property(FormatVideoFormat, Id(format)));
        }

        properties.AddRange(Property(FormatVideoSize, Rectangle(width, height)));
        properties.AddRange(Property(FormatVideoFramerate, Fraction((uint)frameRate, 1)));

        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes(TypeObjectFormat));
        body.AddRange(BitConverter.GetBytes(ParamEnumFormat));
        body.AddRange(properties);

        return Pod(SpaPodType.Object, [.. body]);
    }

    /// <summary>
    /// A request for the cursor to be described separately from the picture.
    /// </summary>
    /// <remarks>
    /// Sent alongside the format, on <c>SPA_PARAM_Meta</c>. The controller draws the pointer itself from the cursor
    /// events, so a pointer painted into the pixels would be a second one, a frame behind. It is a request rather
    /// than a demand: a compositor that will not provide it simply sends no cursor metadata, and the capture path
    /// reports no pointer rather than inventing one.
    /// </remarks>
    public static byte[] CursorMeta(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);

        var properties = new List<byte>();
        properties.AddRange(Property(PipeWireMetaType, Id(PipeWireMetaCursor)));
        properties.AddRange(Property(PipeWireMetaSize, Int(size)));

        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes(TypeObjectParamMeta));
        body.AddRange(BitConverter.GetBytes(PipeWireParamMeta));
        body.AddRange(properties);

        return Pod(SpaPodType.Object, [.. body]);
    }

    /// <summary>SPA_TYPE_OBJECT_ParamBuffers.</summary>
    /// <remarks>
    /// 0x40004, as the daemon prints it. 0x40005 is <see cref="TypeObjectParamMeta"/>; using it here made the buffer
    /// description arrive as a Meta pod with a Buffers id, which PipeWire discarded, leaving the v4l2 source with no
    /// data planes and EINVAL from its buffer allocation.
    /// </remarks>
    public const uint TypeObjectParamBuffers = 262148;

    /// <summary>SPA_TYPE_OBJECT_ParamMeta.</summary>
    public const uint TypeObjectParamMeta = 262149;

    /// <summary>SPA_PARAM_Buffers — how many buffers, how many blocks each, and the memory type.</summary>
    public const uint ParamBuffers = 5;

    /// <summary>SPA_PARAM_BUFFERS_buffers.</summary>
    public const uint BuffersCount = 1;

    /// <summary>SPA_PARAM_BUFFERS_blocks.</summary>
    public const uint BuffersBlocks = 2;

    /// <summary>SPA_PARAM_BUFFERS_dataType — a bitmask of SPA_DATA_* values.</summary>
    public const uint BuffersDataType = 6;

    /// <summary>SPA_DATA_MemPtr — buffers the consumer maps into its own address space.</summary>
    public const int DataTypeMemPtr = 1 << 1;

    /// <summary>SPA_DATA_MemFd — buffers passed as a file descriptor and mapped by the consumer.</summary>
    public const int DataTypeMemFd = 1 << 2;

    /// <summary>
    /// The part of a camera stream's buffer description that only the stream can state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other side — a v4l2 source behind the portal — advertises its own <c>SPA_PARAM_Buffers</c> parameter, and
    /// PipeWire intersects the two sides with <c>spa_pod_filter</c>. The intersection rules are exact rather than
    /// forgiving: two plain-int properties have to be equal. A stream that asked for a 4 MB block against the
    /// source's 2 MB one produced an empty intersection, and <c>pw_buffers_negotiate</c> reported that as
    /// <c>EINVAL</c>, with nothing in the message naming the size. Block size and stride are therefore deliberately
    /// left out here: properties a filter has and this pod does not are copied from the filter, so the allocation is
    /// sized by the source that has to fill it.
    /// </para>
    /// <para>
    /// The data type is the one property that has to come from this side. A v4l2 source's Buffers parameter leaves
    /// <c>SPA_PARAM_BUFFERS_dataType</c> out, and a negotiation that inherits the omission creates buffers whose
    /// type is <c>SPA_ID_INVALID</c>. The v4l2 source tolerates that sentinel and maps them itself; the link mixer
    /// does not, and the input port rejects the buffers with <c>EINVAL</c> after the source has already prepared
    /// them.
    /// </para>
    /// </remarks>
    public static byte[] BufferParams(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var properties = new List<byte>();
        properties.AddRange(Property(BuffersCount, Int(count)));
        properties.AddRange(Property(BuffersBlocks, Int(1)));
        properties.AddRange(Property(BuffersDataType, Int(DataTypeMemPtr | DataTypeMemFd)));

        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes(TypeObjectParamBuffers));
        body.AddRange(BitConverter.GetBytes(ParamBuffers));
        body.AddRange(properties);

        return Pod(SpaPodType.Object, [.. body]);
    }

    /// <summary>SPA_PARAM_Meta.</summary>
    public const uint PipeWireParamMeta = 6;

    /// <summary>SPA_PARAM_META_type.</summary>
    public const uint PipeWireMetaType = 1;

    /// <summary>SPA_PARAM_META_size.</summary>
    public const uint PipeWireMetaSize = 2;

    /// <summary>SPA_META_Cursor.</summary>
    public const uint PipeWireMetaCursor = 5;

    /// <summary>A pod whose body is a single unsigned value, which is how SPA writes an id or an int.</summary>
    private static byte[] Id(uint value) => Pod(SpaPodType.Id, BitConverter.GetBytes(value));

    private static byte[] Int(int value) => Pod(SpaPodType.Int, BitConverter.GetBytes(value));

    /// <summary>A pod whose body is two int32s: a width and a height.</summary>
    private static byte[] Rectangle(int width, int height)
    {
        var body = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(body, width);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), height);
        return Pod(SpaPodType.Rectangle, body);
    }

    /// <summary>A pod whose body is two uint32s: a numerator and a denominator.</summary>
    private static byte[] Fraction(uint numerator, uint denominator)
    {
        var body = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(body, numerator);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), denominator);
        return Pod(SpaPodType.Fraction, body);
    }

    /// <summary>{key, flags} then the value, which starts at the next eight-byte boundary.</summary>
    private static byte[] Property(uint key, byte[] value)
    {
        var bytes = new byte[Align(8) + value.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, key);
        value.CopyTo(bytes, 8);
        return bytes;
    }

    /// <summary>
    /// A pod: the length of the body, its type, the body, and padding out to eight bytes.
    /// </summary>
    /// <remarks>
    /// The length is the body's real length, not its padded length — writing the padded one produces a pod that is
    /// internally consistent and four bytes too long, which the receiving end reads as a following pod in the
    /// padding.
    /// </remarks>
    private static byte[] Pod(SpaPodType type, byte[] body)
    {
        var bytes = new byte[SpaPod.HeaderSize + Align(body.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)type);
        body.CopyTo(bytes, SpaPod.HeaderSize);
        return bytes;
    }

    private static int Align(int size) => (size + 7) & ~7;
}
