using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.TestSupport;

/// <summary>
/// A channel over a stream the test owns. It replaces the copies that differed only in whether they disposed the
/// stream and whether they supplied a type; a channel that swallows everything is <see cref="Stream.Null"/> with the
/// stream-disposal choice left to the caller.
/// </summary>
internal sealed class TestChannel(ChannelType type, Stream? stream = null, bool disposeStream = true) : IChannel
{
    public ChannelType Type => type;

    public Stream Stream => stream ?? System.IO.Stream.Null;

    public ValueTask DisposeAsync() => disposeStream ? Stream.DisposeAsync() : ValueTask.CompletedTask;
}
