using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// Whatever the controller still held when the channel ended is released; one release that fails must not leave the
/// rest stuck.
/// </summary>
public sealed class InputChannelReleaseTests
{
    private static readonly SessionContext TestSession =
        new(SessionId.New(), SessionRole.Host, null!, AccessGrant.Create(SessionMode.Control, Permissions.All, Permissions.All));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_release_that_fails_does_not_stop_the_other_keys_and_buttons_from_being_released()
    {
        var injector = new ThrowingOnFirstReleaseInjector();
        var module = new InputChannelModule(injector, NullLogger<InputChannelModule>.Instance);
        var input = new MemoryStream();
        foreach (var held in new InputEvent[]
        {
            new KeyEvent(0x1D, false, IsDown: true),
            new KeyEvent(0x2A, false, IsDown: true),
            new PointerButtonEvent("d", 0.5, 0.5, PointerButton.Left, IsDown: true),
        })
        {
            await ProtobufFrames.WriteAsync(input, InputMessages.ToMessage(held), Ct);
        }

        input.Position = 0;

        // The channel ends after the last event, so everything above is still held.
        await module.HandleAsync(new TestChannel(input), TestSession, Ct);

        Assert.Equal(3, injector.Releases.Count);
        Assert.All(injector.Releases, release => Assert.False(release is KeyEvent { IsDown: true } or PointerButtonEvent { IsDown: true }));
        Assert.Contains(injector.Releases, release => release is PointerButtonEvent);
    }

    private sealed class ThrowingOnFirstReleaseInjector : IInputInjector
    {
        public List<InputEvent> Releases { get; } = [];

        public bool SupportsSecureAttention => false;

        public void Inject(InputEvent inputEvent)
        {
            var isRelease = inputEvent is KeyEvent { IsDown: false } or PointerButtonEvent { IsDown: false };
            if (!isRelease)
            {
                return;
            }

            var first = Releases.Count == 0;
            Releases.Add(inputEvent);
            if (first)
            {
                throw new InvalidOperationException("The desktop refused the release.");
            }
        }

        public void SendSecureAttentionSequence() => throw new NotSupportedException();
    }

    private sealed class TestChannel(Stream stream) : IChannel
    {
        public ChannelType Type => ChannelType.Input;

        public Stream Stream => stream;

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
