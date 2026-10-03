using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Core.UnitTests;

public class ChannelDispatcherTests
{
    private readonly ChannelDispatcher _dispatcher = new([
        new FakeModule(ChannelType.Video, Permissions.ViewScreen),
        new FakeModule(ChannelType.Input, Permissions.ControlInput),
    ]);

    [Fact]
    public async Task Host_in_watch_mode_rejects_input_channel_and_closes_it()
    {
        var session = CreateSession(SessionRole.Host, SessionMode.View);
        var channel = new FakeChannel(ChannelType.Input);

        var result = await _dispatcher.DispatchAsync(channel, session, TestContext.Current.CancellationToken);

        Assert.Equal(DispatchResult.Denied, result);
        Assert.True(channel.IsDisposed);
    }

    [Fact]
    public async Task Host_in_watch_mode_serves_video()
    {
        var session = CreateSession(SessionRole.Host, SessionMode.View);
        var channel = new FakeChannel(ChannelType.Video);

        var result = await _dispatcher.DispatchAsync(channel, session, TestContext.Current.CancellationToken);

        Assert.Equal(DispatchResult.Accepted, result);
        Assert.True(channel.IsDisposed);
    }

    [Fact]
    public void Unknown_channel_has_no_handler()
    {
        var session = CreateSession(SessionRole.Host, SessionMode.Control);

        Assert.Equal(DispatchResult.NoHandler, _dispatcher.Authorize(ChannelType.Terminal, session, out _));
    }

    [Fact]
    public void Duplicate_modules_for_a_channel_are_rejected() =>
        Assert.Throws<InvalidOperationException>(() => new ChannelDispatcher([
            new FakeModule(ChannelType.Video, Permissions.ViewScreen),
            new FakeModule(ChannelType.Video, Permissions.ViewScreen),
        ]));

    private static SessionContext CreateSession(SessionRole role, SessionMode mode) =>
        new(SessionId.New(), role, connection: null!, AccessGrant.Create(mode, Permissions.All, Permissions.All));

    private sealed class FakeModule(ChannelType type, Permissions permission) : IFeatureModule
    {
        public ChannelType ChannelType => type;

        public Permissions RequiredPermission => permission;

        public Task HandleAsync(IChannel channel, SessionContext session, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeChannel(ChannelType type) : IChannel
    {
        public ChannelType Type => type;

        public Stream Stream { get; } = new MemoryStream();

        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
