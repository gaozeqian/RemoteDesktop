namespace RemoteDesktop.Protocol;

/// <summary>
/// Logical channels multiplexed over one peer connection (one QUIC stream each).
/// Values are on the wire; never renumber, only append.
/// </summary>
public enum ChannelType : byte
{
    Control = 0,
    Video = 1,
    Audio = 2,
    Input = 3,
    Clipboard = 4,
    FileTransfer = 5,
    Terminal = 6,
    Camera = 7,
}
