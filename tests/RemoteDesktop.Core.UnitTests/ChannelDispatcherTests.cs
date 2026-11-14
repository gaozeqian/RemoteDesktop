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

    [Fact]
    public void Host_rejects_a_channel_whose_permission_the_grant_does_not_cover()
    {
        // An unknown high bit is not a real permission: the wire layer masks it off. A module demanding one can
        // therefore never be served from a grant built out of the documented flags.
        var dispatcher = new ChannelDispatcher([new FakeModule(ChannelType.Video, (Permissions)int.MinValue)]);
        var session = CreateSession(SessionRole.Host, SessionMode.Control);

        Assert.Equal(DispatchResult.Denied, dispatcher.Authorize(ChannelType.Video, session, out var module));
        Assert.Null(module);
    }

    [Fact]
    public void A_controller_role_session_is_not_grant_checked_by_the_host_dispatcher()
    {
        // The dispatcher is the host's enforcement point. Documenting the role gate here keeps a future change from
        // silently turning controller-side channels into an authorization decision.
        var session = CreateSession(SessionRole.Controller, SessionMode.View);

        Assert.Equal(DispatchResult.Accepted, _dispatcher.Authorize(ChannelType.Input, session, out _));
    }

    private static SessionContext CreateSession(SessionRole role, SessionMode mode) =>
        new(SessionId.New(), role, connection: null!, AccessGrant.Create(mode, Permissions.All, Permissions.All));

    [Fact]
    public async Task An_accepted_channel_is_handed_to_its_module_exactly_once()
    {
        var module = new FakeModule(ChannelType.Video, Permissions.ViewScreen);
        var dispatcher = new ChannelDispatcher([module]);
        var session = CreateSession(SessionRole.Host, SessionMode.Control);
        var channel = new FakeChannel(ChannelType.Video);

        var result = await dispatcher.DispatchAsync(channel, session, TestContext.Current.CancellationToken);

        Assert.Equal(DispatchResult.Accepted, result);
        Assert.Equal(1, module.Handled);
    }

    private sealed class FakeModule(ChannelType type, Permissions permission) : IFeatureModule
    {
        public ChannelType ChannelType => type;

        public Permissions RequiredPermission => permission;

        public int Handled { get; private set; }

        public Task HandleAsync(IChannel channel, SessionContext session, CancellationToken cancellationToken)
        {
            Handled++;
            return Task.CompletedTask;
        }
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
