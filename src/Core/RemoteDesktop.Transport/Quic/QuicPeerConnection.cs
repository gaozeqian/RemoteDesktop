using System.Net.Quic;
using System.Runtime.Versioning;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Transport.Quic;

/// <summary>Each channel is one QUIC stream whose first byte is the <see cref="ChannelType"/>.</summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed class QuicPeerConnection(
    QuicConnection connection,
    DeviceId remoteDevice,
    ConnectionPath path,
    string localFingerprint,
    string remoteFingerprint,
    string? relaySessionId = null) : IPeerConnection
{
    public DeviceId RemoteDevice { get; } = remoteDevice;

    public ConnectionPath Path { get; } = path;

    public string? RelaySessionId { get; } = relaySessionId;

    public string LocalFingerprint { get; } = localFingerprint;

    public string RemoteFingerprint { get; } = remoteFingerprint;

    public async ValueTask<IChannel> OpenChannelAsync(ChannelType type, bool bidirectional, CancellationToken cancellationToken)
    {
        var stream = await connection.OpenOutboundStreamAsync(
            bidirectional ? QuicStreamType.Bidirectional : QuicStreamType.Unidirectional, cancellationToken).ConfigureAwait(false);
        try
        {
            // The peer only learns about a stream once data arrives, so the header also announces it.
            await stream.WriteAsync(new[] { (byte)type }, cancellationToken).ConfigureAwait(false);
            return new QuicChannel(type, stream);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<IChannel?> AcceptChannelAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            QuicStream stream;
            try
            {
                stream = await connection.AcceptInboundStreamAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (QuicException ex) when (IsClosed(ex))
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }

            var header = new byte[1];
            try
            {
                await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is EndOfStreamException or QuicException)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            var type = (ChannelType)header[0];
            if (!Enum.IsDefined(type))
            {
                // A newer peer offering a channel we do not know: refuse it, keep the connection.
                stream.Abort(QuicAbortDirection.Both, PeerErrorCodes.UnknownChannel);
                await stream.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            return new QuicChannel(type, stream);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await connection.CloseAsync(PeerErrorCodes.ConnectionClosed).ConfigureAwait(false);
        }
        catch (QuicException)
        {
            // Already closed by the peer or by idle timeout.
        }

        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private static bool IsClosed(QuicException ex) =>
        ex.QuicError is QuicError.ConnectionAborted or QuicError.ConnectionIdle or QuicError.ConnectionTimeout or QuicError.OperationAborted;
}

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed class QuicChannel(ChannelType type, QuicStream stream) : IChannel
{
    public ChannelType Type { get; } = type;

    public Stream Stream => stream;

    public ValueTask DisposeAsync() => stream.DisposeAsync();
}
