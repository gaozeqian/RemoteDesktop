namespace RemoteDesktop.Protocol;

/// <summary>
/// Peers exchange versions on the control channel. Same major = compatible; minor additions are negotiated as capabilities.
/// </summary>
/// <remarks>
/// The major was raised to 2 when the password proof started deriving its key with PBKDF2 instead of using the
/// password as the key directly. The two are not interoperable, and a peer that cannot do the new one should say so:
/// without the bump, an old controller meeting a new host would be told its password was wrong, which is both untrue
/// and enough to spend its rate-limit budget.
/// </remarks>
public readonly record struct ProtocolVersion(ushort Major, ushort Minor)
{
    public static ProtocolVersion Current { get; } = new(2, 0);

    public bool IsCompatibleWith(ProtocolVersion other) => Major == other.Major;

    public override string ToString() => $"{Major}.{Minor}";
}
