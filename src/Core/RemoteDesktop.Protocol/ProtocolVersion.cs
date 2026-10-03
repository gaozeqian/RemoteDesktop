namespace RemoteDesktop.Protocol;

/// <summary>
/// Peers exchange versions on the control channel. Same major = compatible; minor additions are negotiated as capabilities.
/// </summary>
public readonly record struct ProtocolVersion(ushort Major, ushort Minor)
{
    public static ProtocolVersion Current { get; } = new(1, 0);

    public bool IsCompatibleWith(ProtocolVersion other) => Major == other.Major;

    public override string ToString() => $"{Major}.{Minor}";
}
