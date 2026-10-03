namespace RemoteDesktop.Abstractions.Input;

/// <summary>Input sent from controller to host. Pointer coordinates are normalized (0..1) to the target display.</summary>
public abstract record InputEvent;

public sealed record PointerMoveEvent(string DisplayId, double X, double Y) : InputEvent;

public sealed record PointerButtonEvent(string DisplayId, double X, double Y, PointerButton Button, bool IsDown) : InputEvent;

public sealed record PointerWheelEvent(string DisplayId, double X, double Y, int DeltaX, int DeltaY) : InputEvent;

/// <param name="ScanCode">Hardware scan code, so the keyboard layout of the host applies.</param>
public sealed record KeyEvent(int ScanCode, bool IsExtended, bool IsDown) : InputEvent;

/// <summary>Text that cannot be expressed as scan codes (IME output, emoji).</summary>
public sealed record TextInputEvent(string Text) : InputEvent;

public enum PointerButton
{
    Left,
    Right,
    Middle,
    X1,
    X2,
}
