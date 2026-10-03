using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Desktop;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Features.Desktop;

/// <summary>
/// Injects controller input on the host. Never reachable in watch mode, because the grant excludes ControlInput.
/// Keys and buttons still held when the channel ends are released, so a dropped connection cannot leave a stuck
/// Ctrl or mouse button behind.
/// </summary>
public sealed partial class InputChannelModule(IInputInjector injector, ILogger<InputChannelModule> logger) : IFeatureModule
{
    public ChannelType ChannelType => ChannelType.Input;

    public Permissions RequiredPermission => Permissions.ControlInput;

    public async Task HandleAsync(IChannel channel, Application.Features.SessionContext session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var pressedKeys = new HashSet<(int ScanCode, bool Extended)>();
        var pressedButtons = new Dictionary<Abstractions.Input.PointerButton, PointerButtonEvent>();
        try
        {
            while (await ProtobufFrames.ReadAsync(channel.Stream, InputMessage.Parser, 4096, cancellationToken).ConfigureAwait(false) is { } message)
            {
                var inputEvent = InputMessages.ToEvent(message);
                switch (inputEvent)
                {
                    case null:
                        continue;
                    case KeyEvent key when key.IsDown:
                        pressedKeys.Add((key.ScanCode, key.IsExtended));
                        break;
                    case KeyEvent key:
                        pressedKeys.Remove((key.ScanCode, key.IsExtended));
                        break;
                    case PointerButtonEvent button when button.IsDown:
                        pressedButtons[button.Button] = button;
                        break;
                    case PointerButtonEvent button:
                        pressedButtons.Remove(button.Button);
                        break;
                    default:
                        break;
                }

                injector.Inject(inputEvent);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // Connection lost: fall through to the release below.
        }
        finally
        {
            foreach (var (scanCode, extended) in pressedKeys)
            {
                injector.Inject(new KeyEvent(scanCode, extended, IsDown: false));
            }

            foreach (var button in pressedButtons.Values)
            {
                injector.Inject(button with { IsDown = false });
            }

            if (pressedKeys.Count + pressedButtons.Count > 0)
            {
                LogReleased(logger, pressedKeys.Count, pressedButtons.Count);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Released {Keys} keys and {Buttons} buttons left pressed by the controller.")]
    private static partial void LogReleased(ILogger logger, int keys, int buttons);
}
