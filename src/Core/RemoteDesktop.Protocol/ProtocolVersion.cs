namespace RemoteDesktop.Protocol;

/// <summary>
/// Peers exchange versions on the control channel. Same major = compatible; minor additions are negotiated as capabilities.
/// </summary>
/// <remarks>
/// The major was raised to 3 when the challenge/HMAC proof was replaced by SRP-6a. The two are not interoperable, and
/// a peer that cannot do the new one must say so rather than be told its password was wrong: that is untrue, spends
/// its rate-limit budget and, before PAKE, encouraged a fallback to the weaker exchange.
/// </remarks>
public readonly record struct ProtocolVersion(ushort Major, ushort Minor)
{
    public static ProtocolVersion Current { get; } = new(3, 0);

    /// <summary>
    /// A version as it arrives on the wire (32-bit fields). A value that does not fit 16 bits saturates instead of
    /// wrapping, so a peer announcing 65539 cannot pass for 3: it ends up as a major that is never the current one.
    /// </summary>
    public static ProtocolVersion FromWire(uint major, uint minor) =>
        new((ushort)Math.Min(major, ushort.MaxValue), (ushort)Math.Min(minor, ushort.MaxValue));

    public bool IsCompatibleWith(ProtocolVersion other) => Major == other.Major;

    public override string ToString() => $"{Major}.{Minor}";
}
