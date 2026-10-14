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
public static class SpaPodBuilder
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
        body.AddRange(BitConverter.GetBytes(TypeObjectFormat));
        body.AddRange(BitConverter.GetBytes(PipeWireParamMeta));
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
