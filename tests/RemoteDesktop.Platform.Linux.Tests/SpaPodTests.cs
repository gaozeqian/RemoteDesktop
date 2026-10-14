using System.Buffers.Binary;
using RemoteDesktop.Platform.Linux.Display;

namespace RemoteDesktop.Platform.Linux.Tests;

/// <summary>
/// Reading PipeWire's serialization format.
/// </summary>
/// <remarks>
/// Built from bytes rather than from a live PipeWire, because that is the point of having this in its own type: the
/// format negotiating that a screen-cast capture depends on is all pod parsing, and none of it should need a running
/// desktop to be checked. The specific keys a format uses are the capture code's business; what is pinned here is
/// the layout, which is what a driver change or a misread offset would break.
/// </remarks>
public sealed class SpaPodTests
{
    private const string LinuxOnly = "The values mirror spa/pod/pod.h.";

    [Fact]
    public void An_id_pod_is_a_length_a_type_and_four_bytes()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // Written out rather than built by a helper, so this pins the layout itself and not just this file's idea of
        // it: four bytes of body, the type word, the value, then padding to the next eight-byte boundary.
        byte[] bytes =
        [
            0x04, 0x00, 0x00, 0x00,   // the body is four bytes long
            0x03, 0x00, 0x00, 0x00,   // SPA_TYPE_Id
            0x08, 0x00, 0x00, 0x00,   // the value
            0x00, 0x00, 0x00, 0x00,   // padding
        ];

        var pod = SpaPod.Parse(bytes);

        Assert.Equal(SpaPodType.Id, pod.Type);
        Assert.Equal(8u, pod.AsId());
        Assert.Equal(4, pod.Size);
    }

    [Fact]
    public void Reading_a_pod_as_the_wrong_kind_throws_rather_than_reinterpreting_the_bytes()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var pod = SpaPod.Parse(Id(8));

        Assert.Throws<InvalidDataException>(() => pod.AsRectangle());
        Assert.Throws<InvalidDataException>(() => pod.AsString());
        Assert.Throws<InvalidDataException>(() => pod.AsProperties());
    }

    [Fact]
    public void A_rectangle_pod_carries_a_width_and_a_height()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var pod = SpaPod.Parse(Rectangle(2560, 1440));

        Assert.Equal(SpaPodType.Rectangle, pod.Type);
        Assert.Equal(new SpaRectangle(2560, 1440), pod.AsRectangle());
    }

    [Fact]
    public void A_string_stops_at_its_padding()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // The stored length includes the trailing null and the padding, which are not part of the value.
        var pod = SpaPod.Parse(Pod(SpaPodType.String, "BGRx\0"u8.ToArray()));

        Assert.Equal("BGRx", pod.AsString());
    }

    [Fact]
    public void A_truncated_pod_is_refused_instead_of_read_past_its_end()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // A length that runs past what was actually received. Trusting it would read whatever follows in the buffer.
        byte[] lying = [0xFF, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00];
        Assert.Throws<InvalidDataException>(() => SpaPod.Parse(lying));

        Assert.Throws<InvalidDataException>(() => SpaPod.Parse(new byte[] { 0x01, 0x02 }));
    }

    [Fact]
    public void An_object_exposes_its_properties_by_key()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // What a format negotiation returns: an object whose properties name what is being set and hold the values
        // allowed for it.
        var bytes = Object(
            objectType: 3, objectId: 3,
            (1u, Id(1)),
            (5u, Rectangle(2560, 1440)));

        var pod = SpaPod.Parse(bytes);
        var properties = pod.AsProperties();

        Assert.Equal(SpaPodType.Object, pod.Type);
        Assert.Equal(2, properties.Count);
        Assert.Equal(1u, properties[0].Key);
        Assert.Equal(SpaPodType.Id, properties[0].Value.Type);
        Assert.Equal(5u, properties[1].Key);
        Assert.Equal(new SpaRectangle(2560, 1440), properties[1].Value.AsRectangle());
    }

    [Fact]
    public void A_property_after_an_odd_sized_one_is_still_found()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // An id body is four bytes, so the next property starts four bytes further on than a reader that ignored the
        // padding would look. Reading it as the next key is the classic way this format gets misparsed.
        var bytes = Object(
            objectType: 3, objectId: 3,
            (1u, Id(1)),
            (2u, Id(2)),
            (3u, Id(3)));

        var properties = SpaPod.Parse(bytes).AsProperties();

        Assert.Equal([1u, 2u, 3u], properties.Select(p => p.Key));
        Assert.Equal([1u, 2u, 3u], properties.Select(p => p.Value.AsId()));
    }

    [Fact]
    public void A_choice_settles_on_its_first_alternative()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // How a driver says "any of these would do" — a range of sizes, a list of formats. The first is the one to
        // take.
        var bytes = Choice(type: 3, Id(2560), Id(1920), Id(1280));

        var pod = SpaPod.Parse(bytes);

        Assert.Equal(SpaPodType.Choice, pod.Type);
        Assert.Equal(2560u, pod.UnwrapChoice().AsId());
    }

    [Fact]
    public void A_choice_with_no_alternatives_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        Assert.Throws<InvalidDataException>(() => SpaPod.Parse(Choice(type: 3)).UnwrapChoice());
    }

    [Fact]
    public void Unwrapping_something_that_is_not_a_choice_gives_it_back()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // Callers ask unconditionally rather than testing the type first, so this has to be harmless.
        var pod = SpaPod.Parse(Rectangle(1920, 1080));

        Assert.Equal(new SpaRectangle(1920, 1080), pod.UnwrapChoice().AsRectangle());
    }

    [Fact]
    public void A_format_inside_a_choice_inside_an_object_is_reachable()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // The shape a real negotiation has: an object whose size property is a choice, because the compositor is
        // offering the sizes it will accept rather than one it insists on.
        var bytes = Object(
            objectType: 3, objectId: 3,
            (5u, Choice(type: 3, Rectangle(2560, 1440), Rectangle(1920, 1080))));

        var properties = SpaPod.Parse(bytes).AsProperties();
        var size = properties.Single(p => p.Key == 5u).Value.UnwrapChoice();

        Assert.Equal(new SpaRectangle(2560, 1440), size.AsRectangle());
    }

    [Fact]
    public void A_struct_lists_its_members()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var bytes = Pod(SpaPodType.Struct, [.. Id(1), .. Id(2), .. Id(3)]);

        var members = SpaPod.Parse(bytes).AsChildren();

        Assert.Equal(3, members.Count);
        Assert.Equal([1u, 2u, 3u], members.Select(m => m.AsId()));
    }

    [Fact]
    public void A_format_request_pod_built_by_pipewire_itself_is_read_correctly()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // Not made up: these are the bytes `spa_pod_builder_add_object` produces for
        // {SPA_FORMAT_VIDEO_format: Id(BGRx), SPA_FORMAT_VIDEO_size: Rectangle(1920, 1080)},
        // dumped from a program compiled against <spa/pod/builder.h>. They are here because the hand-written
        // fixtures around them were built from the same understanding as the parser, and when that understanding
        // was wrong both agreed and both were wrong.
        byte[] bytes =
        [
            0x38, 0x00, 0x00, 0x00, 0x0f, 0x00, 0x00, 0x00, 0x03, 0x00, 0x04, 0x00, 0x03, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00,
            0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x08, 0x00, 0x00, 0x00, 0x0a, 0x00, 0x00, 0x00, 0x80, 0x07, 0x00, 0x00, 0x38, 0x04, 0x00, 0x00,
        ];

        var pod = SpaPod.Parse(bytes);

        Assert.Equal(SpaPodType.Object, pod.Type);
        Assert.Equal(56, pod.Size);

        // 262147 is SPA_TYPE_OBJECT_Format and 3 is SPA_PARAM_EnumFormat.
        Assert.Equal(262147u, pod.AsObjectType());
        Assert.Equal(3u, pod.AsObjectId());

        var properties = pod.AsProperties();
        Assert.Equal(2, properties.Count);

        Assert.Equal(131073u, properties[0].Key);
        Assert.Equal(SpaPodType.Id, properties[0].Value.Type);
        Assert.Equal(8u, properties[0].Value.AsId());

        Assert.Equal(131075u, properties[1].Key);
        Assert.Equal(new SpaRectangle(1920, 1080), properties[1].Value.AsRectangle());
    }

    [Fact]
    public void A_body_that_stops_in_the_middle_of_a_property_ends_the_walk()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // The object header, one whole property, and then a second property's key with no value behind it. The length
        // word is honest — the body really is this long — so the pod itself is well formed and only the properties
        // stop early. There is no count saying how many to expect, so the walk has to notice that the bytes ran out.
        var body = new List<byte>();
        body.AddRange(ObjectHeader(objectType: 262147, objectId: 3));
        body.AddRange(Property(131073u, Id(8)));
        body.AddRange(BitConverter.GetBytes(131075u));

        var properties = SpaPod.Parse(Pod(SpaPodType.Object, [.. body])).AsProperties();

        Assert.Single(properties);
        Assert.Equal(131073u, properties[0].Key);
    }

    // ---- Builders. Each writes the layout as spa/pod/pod.h describes it, so that the cases above stay readable
    // ---- where a literal byte array would not.

    private static byte[] Pod(SpaPodType type, params byte[] body)
    {
        var bytes = new byte[SpaPod.HeaderSize + Align(body.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)type);
        body.CopyTo(bytes, SpaPod.HeaderSize);
        return bytes;
    }

    private static byte[] Id(uint value)
    {
        var body = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, value);
        return Pod(SpaPodType.Id, body);
    }

    private static byte[] Rectangle(int width, int height)
    {
        var body = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (uint)height);
        return Pod(SpaPodType.Rectangle, body);
    }

    private static byte[] Choice(uint type, params byte[][] alternatives)
    {
        var body = new List<byte>();
        var header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, type);

        // child_size is the length of one alternative, and a choice with none is a thing a driver can send even
        // though it is not useful; the builder has to be able to make one for that to be tested.
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), alternatives.Length == 0 ? 0u : (uint)alternatives[0].Length);
        body.AddRange(header);

        foreach (var alternative in alternatives)
        {
            body.AddRange(alternative);
        }

        return Pod(SpaPodType.Choice, [.. body]);
    }

    /// <summary>An object's body starts with its type and id, and nothing else.</summary>
    private static byte[] ObjectHeader(uint objectType, uint objectId)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header, objectType);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), objectId);
        return header;
    }

    /// <summary>A property is a key, four bytes of flags, and the value.</summary>
    private static byte[] Property(uint key, byte[] value)
    {
        var bytes = new byte[8 + value.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, key);
        value.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] Object(uint objectType, uint objectId, params (uint Key, byte[] Value)[] properties)
    {
        var body = new List<byte>();
        body.AddRange(ObjectHeader(objectType, objectId));

        foreach (var (key, value) in properties)
        {
            body.AddRange(Property(key, value));
        }

        return Pod(SpaPodType.Object, [.. body]);
    }

    private static int Align(int size) => (size + 7) & ~7;
}
