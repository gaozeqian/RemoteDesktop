using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Protocol.Desktop;
using ProtoPointerButton = RemoteDesktop.Protocol.Desktop.PointerButton;
using ProtoText = RemoteDesktop.Protocol.Desktop.Text;

namespace RemoteDesktop.Features.Desktop;

/// <summary>Maps the platform-neutral input model to and from its wire form.</summary>
public static class InputMessages
{
    public static InputMessage ToMessage(InputEvent inputEvent) => inputEvent switch
    {
        PointerMoveEvent e => new InputMessage { PointerMove = new PointerMove { DisplayId = e.DisplayId, X = e.X, Y = e.Y } },
        PointerButtonEvent e => new InputMessage
        {
            PointerButton = new ProtoPointerButton { DisplayId = e.DisplayId, X = e.X, Y = e.Y, Button = (PointerButtonMessage)e.Button, Down = e.IsDown },
        },
        PointerWheelEvent e => new InputMessage
        {
            PointerWheel = new PointerWheel { DisplayId = e.DisplayId, X = e.X, Y = e.Y, DeltaX = e.DeltaX, DeltaY = e.DeltaY },
        },
        KeyEvent e => new InputMessage { Key = new Key { ScanCode = (uint)e.ScanCode, Extended = e.IsExtended, Down = e.IsDown } },
        TextInputEvent e => new InputMessage { Text = new ProtoText { Value = e.Text } },
        SecureAttentionEvent => new InputMessage { SecureAttention = new SecureAttention() },
        _ => throw new ArgumentException($"Unsupported input event {inputEvent?.GetType().Name}.", nameof(inputEvent)),
    };

    /// <summary>Returns <c>null</c> for messages this version does not understand (sent by a newer peer).</summary>
    public static InputEvent? ToEvent(InputMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.BodyCase switch
        {
            InputMessage.BodyOneofCase.PointerMove => new PointerMoveEvent(message.PointerMove.DisplayId, Normalize(message.PointerMove.X), Normalize(message.PointerMove.Y)),
            InputMessage.BodyOneofCase.PointerButton when Enum.IsDefined((Abstractions.Input.PointerButton)message.PointerButton.Button) =>
                new PointerButtonEvent(message.PointerButton.DisplayId, Normalize(message.PointerButton.X), Normalize(message.PointerButton.Y),
                    (Abstractions.Input.PointerButton)message.PointerButton.Button, message.PointerButton.Down),
            InputMessage.BodyOneofCase.PointerWheel => new PointerWheelEvent(message.PointerWheel.DisplayId, Normalize(message.PointerWheel.X),
                Normalize(message.PointerWheel.Y), Math.Clamp(message.PointerWheel.DeltaX, -1200, 1200), Math.Clamp(message.PointerWheel.DeltaY, -1200, 1200)),
            InputMessage.BodyOneofCase.Key when message.Key.ScanCode is > 0 and <= 0xFF => new KeyEvent((int)message.Key.ScanCode, message.Key.Extended, message.Key.Down),
            InputMessage.BodyOneofCase.Text when message.Text.Value.Length is > 0 and <= 256 => new TextInputEvent(message.Text.Value),
            InputMessage.BodyOneofCase.SecureAttention => new SecureAttentionEvent(),
            _ => null,
        };
    }

    // Untrusted input: NaN or out-of-range coordinates are clamped instead of reaching the injector.
    private static double Normalize(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}
