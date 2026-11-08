using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.E2E.IntegrationTests;

/// <summary>
/// End-to-end integration tests for remote desktop interaction channels over live cluster sessions,
/// including mouse pointer movement, clicks, mouse wheel, keyboard scancodes,
/// automatic stuck-key release on disconnect, and watch-mode isolation.
/// </summary>
public sealed class DesktopE2ETests(EndToEndClusterFixture cluster) : IClassFixture<EndToEndClusterFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Input_events_reach_host_injector_over_live_control_session()
    {
        var injector = new RecordingInputInjector();
        var module = new InputChannelModule(injector, NullLogger<InputChannelModule>.Instance);

        await using var host = await cluster.StartNodeAsync(new EndToEndNodeOptions { Features = [module] }, Ct);
        await using var controller = await cluster.StartNodeAsync(cancellationToken: Ct);

        await using var session = await controller.ConnectAsync(
            host.Id,
            host.Credentials.TemporaryPassword,
            SessionMode.Control,
            Ct);

        await using var sender = await InputSender.OpenAsync(session.Context.Connection, Ct);

        var moveEvent = new PointerMoveEvent("disp-1", 0.25, 0.75);
        var buttonDown = new PointerButtonEvent("disp-1", 0.25, 0.75, PointerButton.Left, IsDown: true);
        var buttonUp = new PointerButtonEvent("disp-1", 0.25, 0.75, PointerButton.Left, IsDown: false);
        var keyDown = new KeyEvent(0x1E, IsExtended: false, IsDown: true);
        var keyUp = new KeyEvent(0x1E, IsExtended: false, IsDown: false);
        var wheelEvent = new PointerWheelEvent("disp-1", 0.25, 0.75, 0, 120);

        sender.Send(moveEvent);
        sender.Send(buttonDown);
        sender.Send(buttonUp);
        sender.Send(keyDown);
        sender.Send(keyUp);
        sender.Send(wheelEvent);

        await injector.WaitForEventsAsync(6, Ct);

        var events = injector.Events;
        Assert.Equal(moveEvent, events[0]);
        Assert.Equal(buttonDown, events[1]);
        Assert.Equal(buttonUp, events[2]);
        Assert.Equal(keyDown, events[3]);
        Assert.Equal(keyUp, events[4]);
        Assert.Equal(wheelEvent, events[5]);
    }

    [Fact]
    public async Task Held_keys_and_buttons_are_automatically_released_on_session_disconnect()
    {
        var injector = new RecordingInputInjector();
        var module = new InputChannelModule(injector, NullLogger<InputChannelModule>.Instance);

        await using var host = await cluster.StartNodeAsync(new EndToEndNodeOptions { Features = [module] }, Ct);
        await using var controller = await cluster.StartNodeAsync(cancellationToken: Ct);

        var session = await controller.ConnectAsync(
            host.Id,
            host.Credentials.TemporaryPassword,
            SessionMode.Control,
            Ct);

        var sender = await InputSender.OpenAsync(session.Context.Connection, Ct);

        // Controller presses and holds keys and buttons
        var ctrlHold = new KeyEvent(0x1D, IsExtended: false, IsDown: true); // Ctrl down
        var rightMouseHold = new PointerButtonEvent("disp-1", 0.5, 0.5, PointerButton.Right, IsDown: true); // Right button down

        sender.Send(ctrlHold);
        sender.Send(rightMouseHold);

        await injector.WaitForEventsAsync(2, Ct);

        // Abruptly terminate input channel and session
        await sender.DisposeAsync();
        await session.DisposeAsync();

        // Host InputChannelModule must inject synthetic releases for unreleased inputs
        await injector.WaitForEventsAsync(4, Ct);

        var events = injector.Events;
        Assert.Equal(ctrlHold, events[0]);
        Assert.Equal(rightMouseHold, events[1]);

        // Released events: Ctrl up and Right mouse button up
        Assert.Equal(new KeyEvent(0x1D, IsExtended: false, IsDown: false), events[2]);
        Assert.Equal(new PointerButtonEvent("disp-1", 0.5, 0.5, PointerButton.Right, IsDown: false), events[3]);
    }

    [Fact]
    public async Task Watch_mode_session_never_injects_input_to_host()
    {
        var injector = new RecordingInputInjector();
        var module = new InputChannelModule(injector, NullLogger<InputChannelModule>.Instance);

        await using var host = await cluster.StartNodeAsync(new EndToEndNodeOptions { Features = [module] }, Ct);
        await using var controller = await cluster.StartNodeAsync(cancellationToken: Ct);

        // Connect in View-only mode
        await using var session = await controller.ConnectAsync(
            host.Id,
            host.Credentials.TemporaryPassword,
            SessionMode.View,
            Ct);

        // Verify that control input permission was not granted
        Assert.False(session.Context.Grant.Allows(Permissions.ControlInput));

        // Even if a rogue controller attempts to open input sender and stream key events, host refuses channel
        await using var sender = await InputSender.OpenAsync(session.Context.Connection, Ct);
        sender.Send(new KeyEvent(0x1E, IsExtended: false, IsDown: true));
        sender.Send(new PointerButtonEvent("disp-1", 0.5, 0.5, PointerButton.Left, IsDown: true));

        // A pause would only show that nothing had happened yet. The host answers a channel its grant does not cover
        // by closing it, so the controller's own read reaching the end of the stream is the proof the refusal was
        // processed — after that, an event that never arrived is a fact rather than a sampling window.
        await using var refused = await session.Context.Connection.OpenChannelAsync(ChannelType.Input, bidirectional: true, Ct);
        var probe = new byte[1];
        var read = await refused.Stream.ReadAsync(probe, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(0, read);
        Assert.Empty(injector.Events);
    }

    private sealed class RecordingInputInjector : IInputInjector
    {
        private readonly List<InputEvent> _events = [];
        private readonly Lock _lock = new();

        public IReadOnlyList<InputEvent> Events
        {
            get
            {
                lock (_lock)
                {
                    return [.. _events];
                }
            }
        }

        public bool SupportsSecureAttention => false;

        public void Inject(InputEvent inputEvent)
        {
            lock (_lock)
            {
                _events.Add(inputEvent);
            }
        }

        public void SendSecureAttentionSequence() => throw new NotSupportedException();

        public async Task WaitForEventsAsync(int count, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (_lock)
                {
                    if (_events.Count >= count)
                    {
                        return;
                    }
                }

                await Task.Delay(25, cancellationToken);
            }

            Assert.Fail($"Timed out waiting for {count} input events. Received: {Events.Count}");
        }
    }
}
