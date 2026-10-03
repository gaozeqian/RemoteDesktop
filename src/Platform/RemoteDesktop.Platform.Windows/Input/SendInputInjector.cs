using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Input;

namespace RemoteDesktop.Platform.Windows.Input;

/// <summary>
/// Injects input with <c>SendInput</c>. Pointer positions arrive normalized to a display and are converted to the
/// absolute 0..65535 virtual-desktop space; keys are sent as scan codes so the host's keyboard layout applies.
/// </summary>
/// <remarks>
/// Input cannot reach elevated windows or the secure desktop from a normal user process (UIPI); that needs the
/// session agent started by the host service (milestone M3).
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class SendInputInjector(IDisplayEnumerator displays) : IInputInjector
{
    private static readonly TimeSpan DisplayCacheLifetime = TimeSpan.FromSeconds(2);

    private IReadOnlyList<DisplayInfo> _displays = [];
    private DateTime _displaysLoadedAt;

    public void Inject(InputEvent inputEvent)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        switch (inputEvent)
        {
            case PointerMoveEvent move:
                Send(Mouse(move.DisplayId, move.X, move.Y, 0, 0));
                break;
            case PointerButtonEvent button:
                var (flags, data) = ButtonFlags(button.Button, button.IsDown);
                Send(Mouse(button.DisplayId, button.X, button.Y, flags, data));
                break;
            case PointerWheelEvent wheel:
                if (wheel.DeltaY != 0)
                {
                    Send(Mouse(wheel.DisplayId, wheel.X, wheel.Y, MouseWheel, unchecked((uint)wheel.DeltaY)));
                }

                if (wheel.DeltaX != 0)
                {
                    Send(Mouse(wheel.DisplayId, wheel.X, wheel.Y, MouseHWheel, unchecked((uint)wheel.DeltaX)));
                }

                break;
            case KeyEvent key:
                var keyFlags = KeyScanCode | (key.IsExtended ? KeyExtended : 0) | (key.IsDown ? 0 : KeyUp);
                Send(Keyboard(0, (ushort)key.ScanCode, keyFlags));
                break;
            case TextInputEvent text:
                foreach (var unit in text.Text)
                {
                    Send(Keyboard(0, unit, KeyUnicode), Keyboard(0, unit, KeyUnicode | KeyUp));
                }

                break;
            default:
                throw new ArgumentException($"Unsupported input event {inputEvent.GetType().Name}.", nameof(inputEvent));
        }
    }

    public void SendSecureAttentionSequence() =>
        throw new NotSupportedException("Ctrl+Alt+Del requires the host service (milestone M3).");

    private Input Mouse(string displayId, double x, double y, uint flags, uint data)
    {
        var display = FindDisplay(displayId);
        var px = display.Bounds.X + (Math.Clamp(x, 0, 1) * (display.Bounds.Width - 1));
        var py = display.Bounds.Y + (Math.Clamp(y, 0, 1) * (display.Bounds.Height - 1));

        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = Math.Max(2, GetSystemMetrics(SmCxVirtualScreen));
        var height = Math.Max(2, GetSystemMetrics(SmCyVirtualScreen));

        return new Input
        {
            Type = InputMouse,
            Mouse = new MouseInput
            {
                Dx = (int)Math.Round((px - left) * 65535.0 / (width - 1)),
                Dy = (int)Math.Round((py - top) * 65535.0 / (height - 1)),
                MouseData = data,
                Flags = flags | MouseMove | MouseAbsolute | MouseVirtualDesk,
            },
        };
    }

    private static Input Keyboard(ushort virtualKey, ushort scanCode, uint flags) => new()
    {
        Type = InputKeyboard,
        Keyboard = new KeyboardInput { VirtualKey = virtualKey, ScanCode = scanCode, Flags = flags },
    };

    private static (uint Flags, uint Data) ButtonFlags(PointerButton button, bool down) => button switch
    {
        PointerButton.Left => (down ? 0x0002u : 0x0004u, 0),
        PointerButton.Right => (down ? 0x0008u : 0x0010u, 0),
        PointerButton.Middle => (down ? 0x0020u : 0x0040u, 0),
        PointerButton.X1 => (down ? 0x0080u : 0x0100u, 1),
        PointerButton.X2 => (down ? 0x0080u : 0x0100u, 2),
        _ => throw new ArgumentOutOfRangeException(nameof(button)),
    };

    private DisplayInfo FindDisplay(string displayId)
    {
        if (DateTime.UtcNow - _displaysLoadedAt > DisplayCacheLifetime || _displays.Count == 0)
        {
            _displays = displays.GetDisplays();
            _displaysLoadedAt = DateTime.UtcNow;
        }

        DisplayInfo? primary = null;
        foreach (var display in _displays)
        {
            if (display.Id == displayId)
            {
                return display;
            }

            primary ??= display.IsPrimary ? display : null;
        }

        return primary ?? (_displays.Count > 0 ? _displays[0] : throw new InvalidOperationException("No display to inject input into."));
    }

    private static void Send(params ReadOnlySpan<Input> inputs)
    {
        // A zero return means UIPI blocked the input (e.g. an elevated window has focus); there is nothing to retry.
        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseMove = 0x0001;
    private const uint MouseWheel = 0x0800;
    private const uint MouseHWheel = 0x1000;
    private const uint MouseVirtualDesk = 0x4000;
    private const uint MouseAbsolute = 0x8000;
    private const uint KeyExtended = 0x0001;
    private const uint KeyUp = 0x0002;
    private const uint KeyUnicode = 0x0004;
    private const uint KeyScanCode = 0x0008;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, ReadOnlySpan<Input> inputs, int size);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Explicit)]
    private struct Input
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(8)]
        public MouseInput Mouse;

        [FieldOffset(8)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }
}
