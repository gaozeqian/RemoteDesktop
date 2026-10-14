using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Platform.MacOS.Interop;

namespace RemoteDesktop.Platform.MacOS.Input;

/// <summary>
/// Injects input with <c>CGEventPost</c> at the HID event tap — the lowest level, which is where a remote-control
/// tool belongs: events arrive looking like they came from real hardware, so games and remote-desktop-aware apps
/// treat them the same as local input.
/// </summary>
/// <remarks>
/// <para>
/// Requires the Accessibility permission. Without it <c>CGEventPost</c> still returns without an error and nothing
/// happens, which is the worst possible failure mode, so the permission is checked up front and reported.
/// </para>
/// <para>
/// Two things differ from Windows' <c>SendInput</c> and have to be reproduced by hand here. First, dragging: Windows
/// infers a drag from the button being held while a move arrives, while macOS wants a distinct
/// <c>LeftMouseDragged</c>-family event, so the pressed buttons are tracked and the event type chosen accordingly.
/// Second, modifiers: macOS models them as <c>FlagsChanged</c> events with an accumulated flag set that every
/// subsequent key event must carry, rather than as ordinary key downs.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class CGEventInputInjector : IInputInjector, IDisposable
{
    private static readonly TimeSpan DisplayCacheLifetime = TimeSpan.FromSeconds(2);

    private const int ButtonLeft = 1;
    private const int ButtonRight = 2;
    private const int ButtonOther = 4;

    private readonly IDisplayEnumerator _displays;
    private readonly nint _source;
    private readonly Lock _gate = new();

    private IReadOnlyList<DisplayInfo> _cached = [];
    private DateTime _cachedAt;
    private int _pressedButtons;
    private ulong _modifierFlags;

    public CGEventInputInjector(IDisplayEnumerator displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        _displays = displays;
        _source = CoreGraphics.CGEventSourceCreate(CoreGraphics.EventSourceStateHidSystemState);
    }

    /// <summary>Whether this process may post events into other applications.</summary>
    public static bool HasAccessibilityPermission => CoreGraphics.CGPreflightPostEventAccess() != 0;

    /// <summary>Asks the system for Accessibility; the first call shows the prompt.</summary>
    public static bool RequestAccessibilityPermission() => CoreGraphics.CGRequestPostEventAccess() != 0;

    public void Inject(InputEvent inputEvent)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        lock (_gate)
        {
            switch (inputEvent)
            {
                case PointerMoveEvent move:
                    PostPointer(MoveEventType(), Point(move.DisplayId, move.X, move.Y), CoreGraphics.MouseButtonLeft);
                    break;

                case PointerButtonEvent button:
                    PostPointerButton(button);
                    break;

                case PointerWheelEvent wheel:
                    PostWheel(wheel);
                    break;

                case KeyEvent key:
                    PostKey(key);
                    break;

                case TextInputEvent text:
                    PostText(text.Text);
                    break;

                case SecureAttentionEvent:
                    SendSecureAttentionSequence();
                    break;

                default:
                    throw new ArgumentException($"Unsupported input event {inputEvent.GetType().Name}.", nameof(inputEvent));
            }
        }
    }

    /// <summary>False: macOS has no secure attention sequence for a third-party application to raise.</summary>
    public bool SupportsSecureAttention => false;

    public void SendSecureAttentionSequence() =>
        throw new NotSupportedException("macOS has no secure attention sequence to send.");

    public void Dispose()
    {
        if (_source != 0)
        {
            CoreFoundation.CFRelease(_source);
        }
    }

    private void PostPointerButton(PointerButtonEvent button)
    {
        var (flag, number) = ButtonState(button.Button);
        var point = Point(button.DisplayId, button.X, button.Y);

        if (button.IsDown)
        {
            _pressedButtons |= flag;
        }
        else
        {
            _pressedButtons &= ~flag;
        }

        // Left and right have their own event types; everything else shares the "other" family and carries the button
        // number in a field, which is how a five-button mouse is expressed.
        var type = button.Button switch
        {
            PointerButton.Left => button.IsDown ? CoreGraphics.LeftMouseDown : CoreGraphics.LeftMouseUp,
            PointerButton.Right => button.IsDown ? CoreGraphics.RightMouseDown : CoreGraphics.RightMouseUp,
            _ => button.IsDown ? CoreGraphics.OtherMouseDown : CoreGraphics.OtherMouseUp,
        };

        var @event = CoreGraphics.CGEventCreateMouseEvent(_source, type, point, number);
        try
        {
            if (button.Button is not (PointerButton.Left or PointerButton.Right))
            {
                CoreGraphics.CGEventSetIntegerValueField(@event, CoreGraphics.FieldMouseEventButtonNumber, number);
            }

            CoreGraphics.CGEventSetFlags(@event, _modifierFlags);
            CoreGraphics.CGEventPost(CoreGraphics.HidEventTap, @event);
        }
        finally
        {
            CoreFoundation.CFRelease(@event);
        }
    }

    private void PostWheel(PointerWheelEvent wheel)
    {
        // The controller sends Windows wheel deltas (120 per notch), so the notches are what cross over.
        var vertical = wheel.DeltaY / 120;
        var horizontal = wheel.DeltaX / 120;
        if (vertical == 0 && horizontal == 0)
        {
            return;
        }

        var @event = horizontal != 0
            ? CoreGraphics.CGEventCreateScrollWheelEvent(_source, CoreGraphics.ScrollUnitLine, 2, vertical, horizontal)
            : CoreGraphics.CGEventCreateScrollWheelEvent(_source, CoreGraphics.ScrollUnitLine, 1, vertical);

        try
        {
            // The pointer position is carried by the last mouse event, so the scroll lands where the user is looking.
            CoreGraphics.CGEventPost(CoreGraphics.HidEventTap, @event);
        }
        finally
        {
            CoreFoundation.CFRelease(@event);
        }
    }

    private void PostKey(KeyEvent key)
    {
        if (!ScanCodeToMacKey.TryGet(key.ScanCode, key.IsExtended, out var macKey))
        {
            return;
        }

        var modifier = ScanCodeToMacKey.ModifierFlag(macKey);
        if (modifier != 0)
        {
            _modifierFlags = key.IsDown ? _modifierFlags | modifier : _modifierFlags & ~modifier;

            // A modifier is not a key down/up pair: it is one FlagsChanged event whose flags are the new state, and
            // the key code says which modifier changed.
            var changed = CoreGraphics.CGEventCreateKeyboardEvent(_source, macKey, key.IsDown ? (byte)1 : (byte)0);
            try
            {
                CoreGraphics.CGEventSetFlags(changed, _modifierFlags);
                CoreGraphics.CGEventPost(CoreGraphics.HidEventTap, changed);
            }
            finally
            {
                CoreFoundation.CFRelease(changed);
            }

            return;
        }

        var @event = CoreGraphics.CGEventCreateKeyboardEvent(_source, macKey, key.IsDown ? (byte)1 : (byte)0);
        try
        {
            CoreGraphics.CGEventSetFlags(@event, _modifierFlags);
            CoreGraphics.CGEventPost(CoreGraphics.HidEventTap, @event);
        }
        finally
        {
            CoreFoundation.CFRelease(@event);
        }
    }

    private void PostText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        unsafe
        {
            fixed (char* chars = text)
            {
                var down = CoreGraphics.CGEventCreateKeyboardEvent(_source, 0, 1);
                try
                {
                    CoreGraphics.CGEventKeyboardSetUnicodeString(down, (nuint)text.Length, (ushort*)chars);
                    CoreGraphics.CGEventSetFlags(down, _modifierFlags);
                    CoreGraphics.CGEventPost(CoreGraphics.HidEventTap, down);
                }
                finally
                {
                    CoreFoundation.CFRelease(down);
                }
            }
        }
    }

    private void PostPointer(int type, CGPoint point, int button)
    {
        var @event = CoreGraphics.CGEventCreateMouseEvent(_source, type, point, button);
        try
        {
            CoreGraphics.CGEventSetFlags(@event, _modifierFlags);
            CoreGraphics.CGEventPost(CoreGraphics.HidEventTap, @event);
        }
        finally
        {
            CoreFoundation.CFRelease(@event);
        }
    }

    /// <summary>Dragging is not inferred on macOS, so the event type follows whichever button is held.</summary>
    private int MoveEventType()
    {
        if ((_pressedButtons & ButtonLeft) != 0)
        {
            return CoreGraphics.LeftMouseDragged;
        }

        if ((_pressedButtons & ButtonRight) != 0)
        {
            return CoreGraphics.RightMouseDragged;
        }

        return (_pressedButtons & ButtonOther) != 0 ? CoreGraphics.OtherMouseDragged : CoreGraphics.MouseMoved;
    }

    private static (int Flag, int Number) ButtonState(PointerButton button) => button switch
    {
        PointerButton.Left => (ButtonLeft, CoreGraphics.MouseButtonLeft),
        PointerButton.Right => (ButtonRight, CoreGraphics.MouseButtonRight),
        PointerButton.Middle => (ButtonOther, CoreGraphics.MouseButtonCenter),
        PointerButton.X1 => (ButtonOther, 3),
        PointerButton.X2 => (ButtonOther, 4),
        _ => throw new ArgumentOutOfRangeException(nameof(button)),
    };

    /// <summary>Normalized (0..1) position on a display → a point in the global display coordinate space.</summary>
    private CGPoint Point(string displayId, double x, double y)
    {
        var display = FindDisplay(displayId);
        var (pointX, pointY) = PointerCoordinates.FromNormalized(display, x, y);
        return new CGPoint(pointX, pointY);
    }

    private DisplayInfo FindDisplay(string displayId)
    {
        if (DateTime.UtcNow - _cachedAt > DisplayCacheLifetime || _cached.Count == 0)
        {
            _cached = _displays.GetDisplays();
            _cachedAt = DateTime.UtcNow;
        }

        DisplayInfo? primary = null;
        foreach (var display in _cached)
        {
            if (display.Id == displayId)
            {
                return display;
            }

            primary ??= display.IsPrimary ? display : null;
        }

        return primary ?? (_cached.Count > 0 ? _cached[0] : throw new InvalidOperationException("No display to inject input into."));
    }
}
