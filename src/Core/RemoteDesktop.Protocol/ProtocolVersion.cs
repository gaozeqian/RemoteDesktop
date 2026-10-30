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

    public bool IsCompatibleWith(ProtocolVersion other) => Major == other.Major;

    public override string ToString() => $"{Major}.{Minor}";
}
