using System.Buffers.Binary;

namespace RemoteDesktop.Platform.Linux.Display;

/// <summary>The kinds of value a SPA pod can hold. The numbers are from <c>spa/pod/pod.h</c>.</summary>
// Several of these are named after types — Int, Long, Float, Double, String, Object — because that is what
// spa/pod/pod.h calls them, and a pod's type word is exactly this number. Renaming them to suit the analyzer would
// make the one thing this enum has to be checked against, the C header, harder to compare with.
#pragma warning disable CA1720 // Identifier contains type name
public enum SpaPodType : uint
{
    None = 1,
    Bool = 2,
    Id = 3,
    Int = 4,
    Long = 5,
    Float = 6,
    Double = 7,
    String = 8,
    Bytes = 9,
    Rectangle = 10,
    Fraction = 11,
    Bitmap = 12,
    Array = 13,
    Struct = 14,
    Object = 15,
    Sequence = 16,
    Pointer = 17,
    Fd = 18,
    Choice = 19,
    Pod = 20,
}
#pragma warning restore CA1720

/// <summary>A width and a height, as SPA writes one.</summary>
public readonly record struct SpaRectangle(int Width, int Height);

/// <summary>One property of a SPA object: a key, and the value stored under it.</summary>
public readonly record struct SpaProperty(uint Key, SpaPod Value);

/// <summary>
/// A single value in PipeWire's serialization format.
/// </summary>
/// <remarks>
/// <para>
/// Every pod starts with the same two words — the length of what follows and its type — and everything else is built
/// out of pods nested inside those. That is why this type can be one thing rather than a class per kind: a pod is a
/// header, a body, and the knowledge of how to read the body once the type is known.
/// </para>
/// <para>
/// It exists because there is no managed binding for this and the alternative is worse. Negotiating a screen-cast
/// format means reading the answer the compositor gives, and that answer is a pod — a screen size that may be a
/// fixed value, a range, or a list of choices, depending on the driver. Everything the capture path asks about the
/// negotiated format goes through here, so the parsing is in one place and can be tested against bytes rather than
/// against a running desktop.
/// </para>
/// <para>
/// Little-endian throughout, and bodies are padded to eight bytes so that a nested pod always starts aligned.
/// </para>
/// </remarks>
public readonly struct SpaPod
{
    /// <summary>Two words: the body's length, then its type.</summary>
    public const int HeaderSize = 8;

    private readonly ReadOnlyMemory<byte> _body;

    private SpaPod(SpaPodType type, ReadOnlyMemory<byte> body)
    {
        Type = type;
        _body = body;
    }

    /// <summary>The length of the body, not counting the header.</summary>
    public int Size => _body.Length;

    public SpaPodType Type { get; }

    /// <summary>Reads the pod at the start of <paramref name="data"/>.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a pod, or the length runs past the end.</exception>
    public static SpaPod Parse(ReadOnlyMemory<byte> data)
    {
        if (data.Length < HeaderSize)
        {
            throw new InvalidDataException($"A pod needs at least {HeaderSize} bytes; got {data.Length}.");
        }

        // The header is the length word followed by the type word, so the type is at four, not at HeaderSize — an
        // easy four bytes to be out by, and reading there instead gets the body's first word, which is a plausible
        // small number and therefore a wrong type that looks like a right one.
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data.Span);
        var type = BinaryPrimitives.ReadUInt32LittleEndian(data.Span[4..]);

        if (size > data.Length - HeaderSize)
        {
            throw new InvalidDataException($"A pod claims {size} bytes of body but only {data.Length - HeaderSize} are present.");
        }

        return new SpaPod((SpaPodType)type, data.Slice(HeaderSize, (int)size));
    }

    /// <summary>Reads the value of a pod that holds an id, which is how PipeWire names formats and enum values.</summary>
    /// <exception cref="InvalidDataException">This pod holds something else.</exception>
    public uint AsId() => Require(SpaPodType.Id) is { Length: >= 4 } body
        ? BinaryPrimitives.ReadUInt32LittleEndian(body.Span)
        : throw new InvalidDataException("An id pod is shorter than an id.");

    /// <summary>Reads the value of a pod that holds an int.</summary>
    /// <exception cref="InvalidDataException">This pod holds something else.</exception>
    public int AsInt() => Require(SpaPodType.Int) is { Length: >= 4 } body
        ? BinaryPrimitives.ReadInt32LittleEndian(body.Span)
        : throw new InvalidDataException("An int pod is shorter than an int.");

    /// <summary>Reads a width and a height.</summary>
    /// <exception cref="InvalidDataException">This pod holds something else.</exception>
    public SpaRectangle AsRectangle()
    {
        var body = Require(SpaPodType.Rectangle);
        if (body.Length < 8)
        {
            throw new InvalidDataException("A rectangle pod is shorter than two numbers.");
        }

        return new SpaRectangle(
            (int)BinaryPrimitives.ReadUInt32LittleEndian(body.Span),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(body.Span[4..]));
    }

    /// <summary>Reads a string. PipeWire pads these with a trailing null that is not part of the value.</summary>
    /// <exception cref="InvalidDataException">This pod holds something else.</exception>
    public string AsString()
    {
        var body = Require(SpaPodType.String).Span;
        var end = body.IndexOf((byte)0);
        return System.Text.Encoding.UTF8.GetString(end < 0 ? body : body[..end]);
    }

    /// <summary>
    /// The value a <see cref="SpaPodType.Choice"/> settles on: its first alternative.
    /// </summary>
    /// <remarks>
    /// A choice is how a driver says "any of these would do" — a range of frame sizes, a list of pixel formats. The
    /// first entry is the one to take; the rest are alternatives to fall back to. On anything that is not a choice,
    /// this returns the pod unchanged, so callers can ask unconditionally rather than testing first.
    /// </remarks>
    public SpaPod UnwrapChoice()
    {
        if (Type != SpaPodType.Choice)
        {
            return this;
        }

        // A choice body is {type, flags, child_size, child_type} followed by its alternatives.
        const int ChoiceHeaderSize = 16;
        if (_body.Length < ChoiceHeaderSize)
        {
            throw new InvalidDataException("A choice pod is shorter than its header.");
        }

        var first = Children(_body[ChoiceHeaderSize..]).GetEnumerator();
        return first.MoveNext() ? first.Current : throw new InvalidDataException("A choice pod offers no alternatives.");
    }

    /// <summary>The elements of an array or the members of a struct.</summary>
    /// <exception cref="InvalidDataException">This pod holds something else.</exception>
    public IReadOnlyList<SpaPod> AsChildren()
    {
        if (Type == SpaPodType.Struct)
        {
            return [.. Children(_body)];
        }

        if (Type == SpaPodType.Array)
        {
            // An array body is {child_size, child_type} followed by the elements.
            const int ArrayHeaderSize = 8;
            if (_body.Length < ArrayHeaderSize)
            {
                throw new InvalidDataException("An array pod is shorter than its header.");
            }

            return [.. Children(_body[ArrayHeaderSize..])];
        }

        throw new InvalidDataException($"A {Type} pod has no children.");
    }

    /// <summary>
    /// The properties of an object, which is how a format is described: a key naming what is being set, and the
    /// value or values allowed for it.
    /// </summary>
    /// <exception cref="InvalidDataException">This pod holds something else.</exception>
    public IReadOnlyList<SpaProperty> AsProperties()
    {
        if (Type != SpaPodType.Object)
        {
            throw new InvalidDataException($"A {Type} pod has no properties.");
        }

        // {object_type, object_id} and then the properties, to the end of the body. There is no count — an earlier
        // version of this read one from the word after the id, which is not a count but the first property's key.
        // With `SPA_FORMAT_VIDEO_format` in that position that is 131073, so the parser confidently went looking for
        // a hundred thousand properties. The mistake survived its tests because the test bytes had been written from
        // the same wrong idea; it was caught by building a pod with the real builder and comparing the bytes.
        const int ObjectHeaderSize = 8;
        if (_body.Length < ObjectHeaderSize)
        {
            throw new InvalidDataException("An object pod is shorter than its header.");
        }

        var remaining = _body[ObjectHeaderSize..];
        var properties = new List<SpaProperty>();
        var offset = 0;

        // Until the body runs out, which is the only thing that says how many there are. A body that ends in the
        // middle of a property ends the walk rather than reading past it.
        while (offset + 8 <= remaining.Length)
        {
            var key = BinaryPrimitives.ReadUInt32LittleEndian(remaining.Span[offset..]);

            // {key, flags} then the value, which starts at the next eight-byte boundary.
            var valueOffset = Align(offset + 8);
            if (valueOffset + HeaderSize > remaining.Length)
            {
                break;
            }

            properties.Add(new SpaProperty(key, Parse(remaining[valueOffset..])));

            offset = Align(valueOffset + HeaderSize + (int)BinaryPrimitives.ReadUInt32LittleEndian(remaining.Span[valueOffset..]));
        }

        return properties;
    }

    /// <summary>The object's kind, which for a negotiated format is <c>SPA_TYPE_OBJECT_Format</c>.</summary>
    /// <exception cref="InvalidDataException">This pod holds something else.</exception>
    public uint AsObjectType() => Require(SpaPodType.Object) is { Length: >= 8 } body
        ? BinaryPrimitives.ReadUInt32LittleEndian(body.Span)
        : throw new InvalidDataException("An object pod is shorter than its header.");

    /// <summary>What the object is an instance of, such as <c>SPA_PARAM_EnumFormat</c>.</summary>
    /// <exception cref="InvalidDataException">This pod holds something else.</exception>
    public uint AsObjectId() => Require(SpaPodType.Object) is { Length: >= 8 } body
        ? BinaryPrimitives.ReadUInt32LittleEndian(body.Span[4..])
        : throw new InvalidDataException("An object pod is shorter than its header.");

    /// <summary>Walks a run of pods laid end to end, each starting at the next eight-byte boundary.</summary>
    private static IEnumerable<SpaPod> Children(ReadOnlyMemory<byte> body)
    {
        var offset = 0;
        while (offset + HeaderSize <= body.Length)
        {
            var pod = Parse(body[offset..]);
            yield return pod;
            offset = Align(offset + HeaderSize + pod.Size);
        }
    }

    private static int Align(int offset) => (offset + 7) & ~7;

    private ReadOnlyMemory<byte> Require(SpaPodType expected) => Type == expected
        ? _body
        : throw new InvalidDataException($"Expected a {expected} pod but found a {Type} one.");
}
