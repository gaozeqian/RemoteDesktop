using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;
using RemoteDesktop.Transport.Quic;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// A controller whose link breaks in the middle of the handshake, before it has shown any password evidence, must not
/// use up the attempts the host allows it: the host reserves an attempt when the handshake starts and has to give it
/// back, uncounted, however the handshake ends.
/// </summary>
public sealed class HandshakeInterruptionTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Handshakes_that_break_before_the_password_evidence_do_not_count_and_the_device_can_still_authenticate()
    {
        var link = new BreakableLink();
        await using var host = await TestDevice.StartAsync(server);
        await using var controller = await TestDevice.StartAsync(server, services =>
        {
            services.AddSingleton<QuicPeerConnector>();
            services.AddSingleton<IPeerConnector>(sp => new BreakingConnector(sp.GetRequiredService<QuicPeerConnector>(), link));
        });

        // Well past the number of failures the host tolerates: if a broken handshake counted, the device would be
        // locked out long before the last of these.
        link.Broken = true;
        for (var i = 0; i < AuthAttemptLimiter.MaxFailuresPerDevice * 2; i++)
        {
            await Assert.ThrowsAsync<IOException>(
                () => controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct));
        }

        await WaitForAsync(() => host.Limiter.PendingReservations == 0, "the host to give back every reservation");

        link.Broken = false;
        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);

        Assert.Equal(SessionMode.Control, session.Context.Grant.Mode);
        await WaitForAsync(() => host.Limiter.PendingReservations == 0, "the successful handshake to settle its reservation");
    }

    /// <summary>
    /// A controller that opens the control channel and then goes silent holds one of the host's handshake slots and one
    /// of the device's reserved attempts. The host gives up after its handshake timeout and must give both back,
    /// uncounted: a controller that only stalls has shown no password evidence, so it has not failed.
    /// </summary>
    [Fact]
    public async Task A_handshake_that_stalls_is_given_up_on_and_does_not_count_against_the_device()
    {
        var link = new StallableLink();
        await using var host = await TestDevice.StartAsync(server);
        host.HostSessions.HandshakeTimeout = TimeSpan.FromSeconds(1);
        await using var controller = await TestDevice.StartAsync(server, services =>
        {
            services.AddSingleton<QuicPeerConnector>();
            services.AddSingleton<IPeerConnector>(sp => new StallingConnector(sp.GetRequiredService<QuicPeerConnector>(), link));
        });

        // More stalled handshakes than failures the host tolerates: if a stall counted, the device would be locked out.
        link.Stalled = true;
        for (var i = 0; i < AuthAttemptLimiter.MaxFailuresPerDevice + 1; i++)
        {
            using var stalled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var attempt = controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, stalled.Token);

            // The reservation is held while the controller is silent ...
            await WaitForAsync(() => host.Limiter.PendingReservations == 1, "the host to reserve an attempt");

            // ... and returned by the host's own timeout, not by anything the controller does.
            // Eight seconds is far above the one-second timeout and far below the real 15 s one, so only the timeout
            // this test set can satisfy it.
            await WaitForAsync(() => host.Limiter.PendingReservations == 0, "the host to give up on the silent controller", seconds: 8);

            // Whichever way the controller finds out (the host's abort of the stream, or its own cancellation), the
            // attempt did not succeed; what matters is the host's state, checked above.
            await stalled.CancelAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => attempt);
        }

        // The short timeout was only to make the stalls quick. A real handshake (QUIC and the password exchange) can take
        // longer than that under load, so the last one runs under the host's normal timeout.
        link.Stalled = false;
        host.HostSessions.HandshakeTimeout = TimeSpan.FromSeconds(15);
        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);

        Assert.Equal(SessionMode.Control, session.Context.Grant.Mode);
    }

    private static async Task WaitForAsync(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(50, Ct);
        }
    }

    private sealed class BreakableLink
    {
        public volatile bool Broken;
    }

    private sealed class BreakingConnector(IPeerConnector inner, BreakableLink link) : IPeerConnector
    {
        public async Task<IPeerConnection> ConnectAsync(DeviceId remote, CancellationToken cancellationToken) =>
            new BreakingConnection(await inner.ConnectAsync(remote, cancellationToken), link);
    }

    private sealed class BreakingConnection(IPeerConnection inner, BreakableLink link) : IPeerConnection
    {
        public DeviceId RemoteDevice => inner.RemoteDevice;

        public ConnectionPath Path => inner.Path;

        public string? RelaySessionId => inner.RelaySessionId;

        public string LocalFingerprint => inner.LocalFingerprint;

        public string RemoteFingerprint => inner.RemoteFingerprint;

        public async ValueTask<IChannel> OpenChannelAsync(ChannelType type, bool bidirectional, CancellationToken cancellationToken)
        {
            var channel = await inner.OpenChannelAsync(type, bidirectional, cancellationToken);
            return type == ChannelType.Control && link.Broken ? new BreakingChannel(channel) : channel;
        }

        public ValueTask<IChannel?> AcceptChannelAsync(CancellationToken cancellationToken) => inner.AcceptChannelAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class BreakingChannel(IChannel inner) : IChannel
    {
        public ChannelType Type => inner.Type;

        public Stream Stream { get; } = new BreakingStream(inner.Stream);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>
    /// Passes everything through until the host's challenge has been read; the next write - the one that would carry
    /// the password evidence - fails the way a dropped link does.
    /// </summary>
    private sealed class BreakingStream(Stream inner) : Stream
    {
        private volatile bool _challengeRead;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            _challengeRead |= read > 0;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            _challengeRead |= read > 0;
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfBroken();
            inner.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfBroken();
            return inner.WriteAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        private void ThrowIfBroken()
        {
            if (_challengeRead)
            {
                throw new IOException("The link broke before the password evidence was sent.");
            }
        }
    }

    private sealed class StallableLink
    {
        public volatile bool Stalled;
    }

    private sealed class StallingConnector(IPeerConnector inner, StallableLink link) : IPeerConnector
    {
        public async Task<IPeerConnection> ConnectAsync(DeviceId remote, CancellationToken cancellationToken) =>
            new StallingConnection(await inner.ConnectAsync(remote, cancellationToken), link);
    }

    private sealed class StallingConnection(IPeerConnection inner, StallableLink link) : IPeerConnection
    {
        public DeviceId RemoteDevice => inner.RemoteDevice;

        public ConnectionPath Path => inner.Path;

        public string? RelaySessionId => inner.RelaySessionId;

        public string LocalFingerprint => inner.LocalFingerprint;

        public string RemoteFingerprint => inner.RemoteFingerprint;

        public async ValueTask<IChannel> OpenChannelAsync(ChannelType type, bool bidirectional, CancellationToken cancellationToken)
        {
            var channel = await inner.OpenChannelAsync(type, bidirectional, cancellationToken);
            return type == ChannelType.Control && link.Stalled ? new StallingChannel(channel) : channel;
        }

        public ValueTask<IChannel?> AcceptChannelAsync(CancellationToken cancellationToken) => inner.AcceptChannelAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class StallingChannel(IChannel inner) : IChannel
    {
        public ChannelType Type => inner.Type;

        public Stream Stream { get; } = new StallingStream(inner.Stream);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>
    /// Passes everything through until the host's challenge has been read; after that every write - the one that would
    /// carry the password evidence - waits until the caller gives up, the way a link that went silent does.
    /// </summary>
    private sealed class StallingStream(Stream inner) : Stream
    {
        private volatile bool _challengeRead;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            _challengeRead |= read > 0;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            _challengeRead |= read > 0;
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_challengeRead)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
