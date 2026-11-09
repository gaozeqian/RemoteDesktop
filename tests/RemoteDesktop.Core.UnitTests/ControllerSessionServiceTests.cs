using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Core.UnitTests;

public sealed class ControllerSessionServiceTests
{
    [Fact]
    public async Task A_peer_that_never_answers_the_challenge_times_out_and_is_disposed()
    {
        var connection = new SilentConnection();
        var service = Service(connection, TimeSpan.FromMilliseconds(25));

        var error = await Assert.ThrowsAsync<SessionException>(() =>
            service.ConnectAsync(new DeviceId(123456789), "password", SessionMode.Control, CancellationToken.None));

        Assert.Equal(SessionFailure.Timeout, error.Failure);
        Assert.True(connection.Disposed);
        Assert.True(connection.Channel.Disposed);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_misreported_as_a_peer_timeout()
    {
        var connection = new SilentConnection();
        var service = Service(connection, TimeSpan.FromSeconds(30));
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ConnectAsync(new DeviceId(123456789), "password", SessionMode.Control, stop.Token));
        Assert.True(connection.Disposed);
        Assert.True(connection.Channel.Disposed);
    }

    private static ControllerSessionService Service(IPeerConnection connection, TimeSpan timeout) =>
        new(
            new FakeConnector(connection),
            new SessionTelemetry(new TelemetryOptions { Enabled = false }, null, TimeProvider.System),
            new SessionLifecycle(null),
            TimeProvider.System,
            timeout);

    private sealed class FakeConnector(IPeerConnection connection) : IPeerConnector
    {
        public Task<IPeerConnection> ConnectAsync(DeviceId remote, CancellationToken cancellationToken) =>
            Task.FromResult(connection);
    }

    private sealed class SilentConnection : IPeerConnection
    {
        public SilentConnection() => Channel = new SilentChannel();

        public SilentChannel Channel { get; }

        public bool Disposed { get; private set; }

        public DeviceId RemoteDevice => new(123456789);

        public ConnectionPath Path => ConnectionPath.Direct;

        public string? RelaySessionId => null;

        public string LocalFingerprint => "local";

        public string RemoteFingerprint => "remote";

        public ValueTask<IChannel> OpenChannelAsync(
            ChannelType type,
            bool bidirectional,
            CancellationToken cancellationToken) => ValueTask.FromResult<IChannel>(Channel);

        public ValueTask<IChannel?> AcceptChannelAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IChannel?>(null);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SilentChannel : IChannel
    {
        private readonly SilentStream _stream = new();

        public ChannelType Type => ChannelType.Control;

        public Stream Stream => _stream;

        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return _stream.DisposeAsync();
        }
    }

    private sealed class SilentStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position { get; set; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }
}
