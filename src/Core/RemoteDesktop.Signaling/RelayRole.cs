namespace RemoteDesktop.Signaling;

/// <summary>First byte of a <see cref="UdpControlPacketType.RelayBind"/> body; the rest is the ticket.</summary>
public enum RelayRole : byte
{
    Controller = 1,
    Host = 2,
}
