using RemoteDesktop.Application.Features;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// The session's loops start before its owner can subscribe; the end of a session that is already over must still reach
/// a subscriber that arrives afterwards, exactly once.
/// </summary>
public sealed class RemoteSessionClosedTests
{
    [Fact]
    public async Task A_handler_added_after_the_session_ended_is_called_once()
    {
        var control = new StubChannel();
        var context = new SessionContext(
            SessionId.New(),
            SessionRole.Controller,
            new StubConnection(),
            AccessGrant.Create(SessionMode.Control, Permissions.All, Permissions.All));
        var session = RemoteSession.Start(context, control, TimeProvider.System);
        await session.DisposeAsync();

        var calls = 0;
        session.Closed += (_, _) => calls++;

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_handler_added_before_the_end_is_called_once()
    {
        var context = new SessionContext(
            SessionId.New(),
            SessionRole.Controller,
            new StubConnection(),
            AccessGrant.Create(SessionMode.Control, Permissions.All, Permissions.All));
        var session = RemoteSession.Start(context, new StubChannel(), TimeProvider.System);
        var calls = 0;
        session.Closed += (_, _) => calls++;

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(1, calls);
    }

    private sealed class StubChannel : IChannel
    {
        public ChannelType Type => ChannelType.Control;

        // Reads as an already closed channel and swallows writes.
        public Stream Stream { get; } = Stream.Null;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubConnection : IPeerConnection
    {
        public DeviceId RemoteDevice => new(123456789);

        public ConnectionPath Path => ConnectionPath.Direct;

        public string? RelaySessionId => null;

        public string LocalFingerprint => "local";

        public string RemoteFingerprint => "remote";

        public ValueTask<IChannel> OpenChannelAsync(ChannelType type, bool bidirectional, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<IChannel?> AcceptChannelAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IChannel?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
