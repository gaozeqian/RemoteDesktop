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
        var sessionKey = session.Id.ToString();
        try
        {
            while (await ProtobufFrames.ReadAsync(channel.Stream, InputMessage.Parser, 4096, cancellationToken).ConfigureAwait(false) is { } message)
            {
                var inputEvent = InputMessages.ToEvent(message);
                if (inputEvent is SecureAttentionEvent && !session.Grant.Allows(Permissions.SecureAttention))
                {
                    LogSecureAttentionRefused(logger, session.Id);
                    continue;
                }

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

                try
                {
                    injector.Inject(inputEvent, sessionKey);
                }
                catch (NotSupportedException ex)
                {
                    // E.g. Ctrl+Alt+Del without the host service: ignore it, keep serving the rest.
                    LogUnsupported(logger, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // Connection lost: fall through to the release below.
        }
        finally
        {
            // Each release stands alone: one that fails must neither leave the others held nor replace the exception
            // this block is unwinding with.
            foreach (var (scanCode, extended) in pressedKeys)
            {
                Release(new KeyEvent(scanCode, extended, IsDown: false), sessionKey);
            }

            foreach (var button in pressedButtons.Values)
            {
                Release(button with { IsDown = false }, sessionKey);
            }

            if (pressedKeys.Count + pressedButtons.Count > 0)
            {
                LogReleased(logger, pressedKeys.Count, pressedButtons.Count);
            }

            injector.ReleaseSession(sessionKey);
        }
    }

    private void Release(InputEvent release, string sessionKey)
    {
        try
        {
            injector.Inject(release, sessionKey);
        }
#pragma warning disable CA1031 // The remaining keys and buttons must still be released.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogReleaseFailed(logger, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not release a key or button left pressed by the controller: {Reason}")]
    private static partial void LogReleaseFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId}: refused Ctrl+Alt+Del without SecureAttention permission.")]
    private static partial void LogSecureAttentionRefused(ILogger logger, SessionId sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Input ignored: {Reason}")]
    private static partial void LogUnsupported(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Released {Keys} keys and {Buttons} buttons left pressed by the controller.")]
    private static partial void LogReleased(ILogger logger, int keys, int buttons);
}
