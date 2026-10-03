namespace RemoteDesktop.Protocol.Framing;

/// <param name="MessageType">Channel-specific message discriminator.</param>
public readonly record struct Frame(ushort MessageType, ReadOnlyMemory<byte> Payload);
