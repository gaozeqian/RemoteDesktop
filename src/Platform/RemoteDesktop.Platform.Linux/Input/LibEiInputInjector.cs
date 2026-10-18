using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Platform.Linux.Interop;
using RemoteDesktop.Platform.Linux.XdgPortal;

namespace RemoteDesktop.Platform.Linux.Input;

/// <summary>
/// Injects input by way of libei, over a descriptor the desktop portal hands over.
/// </summary>
/// <remarks>
/// <para>
/// This is the Wayland equivalent of what Windows does with <c>SendInput</c>, and it exists so that this end never
/// needs write access to <c>/dev/uinput</c>. That would be a machine-wide privilege — permanent permission to type
/// into any session on the computer, with no consent and no way to take it back — granted to whoever runs this, in
/// exchange for a feature that the desktop already has a way to allow. libei is that way: the compositor agrees to
/// accept input from this connection, and it can stop.
/// </para>
/// <para>
/// <b>The connection is opened on the first injection, and opening it asks the operator.</b> A remote-desktop
/// session cannot be persisted — the portal refuses it — so unlike screen sharing there is no remembered grant, and
/// a controller that wants to type will cause a dialog the first time. That is a deliberate restriction rather than
/// an oversight: watching a screen and controlling one are not the same permission.
/// </para>
/// <para>
/// Everything here happens on a thread of its own, because libei is a socket that has to be drained to learn what
/// the device can do, and the answer arrives asynchronously. Injections are written from whichever thread calls
/// them, under a lock, and committed with a frame — libei batches a frame's worth of events into one write.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class LibEiInputInjector : IInputInjector, IDisposable
{
    /// <summary>BTN_LEFT and the four to its right, from linux/input-event-codes.h.</summary>
    private const uint ButtonLeft = 0x110;
    private const uint ButtonRight = 0x111;
    private const uint ButtonMiddle = 0x112;
    private const uint ButtonSide = 0x113;
    private const uint ButtonExtra = 0x114;

    /// <summary>One notch of a wheel, which is how Windows counts too — the protocol carries 120ths.</summary>
    private const int WheelNotch = 120;

    private readonly PortalControlSession _control;
    private readonly ILogger _logger;
    private readonly IDisplayEnumerator _displays;
    private readonly Lock _gate = new();
    private nint _ei;
    // One per capability, because the desktop hands out separate devices rather than one that can do everything:
    // on this machine a pointer with buttons and scroll arrives first, and the keyboard separately. Keeping a single
    // "current device" means whichever arrived last wins, and a pointer move sent to the keyboard device is dropped
    // without complaint.
    private nint _pointerDevice;
    private nint _buttonDevice;
    private nint _keyboardDevice;
    private nint _scrollDevice;
    private Thread? _pump;
    private volatile bool _running;
    private uint _sequence;
    private IReadOnlyList<DisplayInfo> _cachedDisplays = [];
    private DateTime _cachedAt;

    public LibEiInputInjector(IDisplayEnumerator displays, PortalControlSession control, ILogger<LibEiInputInjector> logger)
    {
        _displays = displays;
        _control = control;
        _logger = logger;
    }

    public void Inject(InputEvent inputEvent)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);

        if (inputEvent is SecureAttentionEvent)
        {
            SendSecureAttentionSequence();
            return;
        }

        lock (_gate)
        {
            // Not an error. The first injection is what starts the conversation with the desktop, and injection
            // arrives continuously; losing the events that arrive while the operator is reading a dialog is better
            // than blocking the channel that carries them.
            if (!Ready && !EnsureConnected())
            {
                return;
            }

            // Text does not go through libei at all — it is a call on the portal — so it has no frame to commit.
            if (inputEvent is TextInputEvent typed)
            {
                SendText(typed.Text);
                return;
            }

            // Each operation reports which device it wrote to, so that device can be framed. libei commits per
            // device, and framing the wrong one loses the event without complaint.
            var used = inputEvent switch
            {
                PointerMoveEvent move => MovePointer(move.DisplayId, move.X, move.Y),
                PointerButtonEvent button => PressButton(button),
                PointerWheelEvent wheel => Scroll(wheel),
                KeyEvent key => SendKey(key),
                _ => 0,
            };

            if (used == 0)
            {
                LogUnknownEvent(_logger, inputEvent.GetType().Name);
                return;
            }

            LibEi.DeviceFrame(used, LibEi.Now(_ei));
        }
    }

    /// <summary>
    /// Ctrl+Alt+Del, which does not exist here.
    /// </summary>
    /// <remarks>
    /// The secure attention sequence is a Windows notion — a keystroke the operating system intercepts before any
    /// application can see it, so that only the real system can ask for a password. Wayland has no equivalent, and
    /// a session lock is a different user's session that this process cannot see or reach.
    /// </remarks>
    /// <summary>False: a Wayland session has no secure attention sequence for an application to raise.</summary>
    public bool SupportsSecureAttention => false;

    public void SendSecureAttentionSequence() =>
        throw new NotSupportedException("A Wayland session has no secure attention sequence to send.");

    /// <summary>Whether the desktop has offered a device that anything can be sent on.</summary>
    private bool Ready => _pointerDevice != 0 || _buttonDevice != 0 || _keyboardDevice != 0 || _scrollDevice != 0;

    public void Dispose()
    {
        lock (_gate)
        {
            _running = false;
            _pump?.Join(TimeSpan.FromSeconds(2));
            _pump = null;

            if (_ei != 0)
            {
                LibEi.Unref(_ei);
                _ei = 0;
            }

            _pointerDevice = _buttonDevice = _keyboardDevice = _scrollDevice = 0;
        }
    }

    /// <summary>
    /// Connects libei to the session that carries control, and says whether input can be sent yet.
    /// </summary>
    /// <remarks>
    /// The session itself belongs to <see cref="PortalControlSession"/>, which asks the desktop for it once and
    /// shares it with the clipboard — the portal allows the clipboard to be requested once per session and only
    /// before it starts, so there has to be one session and not one per feature. What is left here is libei's own
    /// handshake. It returns <c>false</c> the first time even on success: the descriptor is taken and the pump
    /// started, but the device arrives asynchronously, so there is nothing to send on yet.
    /// </remarks>
    private bool EnsureConnected()
    {
        // The descriptor can be connected once per portal session. The device it announces arrives asynchronously,
        // so more input can arrive before Ready; asking for another descriptor then is refused by the portal
        // ("Session is already connected") and used to end the whole input channel. Waiting is what the caller
        // already does with events that arrive before the device does.
        if (_ei != 0)
        {
            return Ready;
        }

        if (!_control.EnsureReady())
        {
            return false;
        }

        try
        {
            using var descriptor = _control.ConnectEis();

            _ei = LibEi.NewSender(0);
            if (_ei == 0)
            {
                throw new InvalidOperationException("libei would not create a sender.");
            }

            _ = LibEi.ConfigureName(_ei, "RemoteDesktop");

            // libei owns the descriptor from here. Marking the handle invalid stops it being closed when the
            // wrapper is disposed — which would close a descriptor that is now libei's, and that the process may
            // since have handed to something else.
            var raw = (int)descriptor.DangerousGetHandle();
            descriptor.SetHandleAsInvalid();

            var setup = LibEi.SetupBackendFd(_ei, raw);
            if (setup < 0)
            {
                throw new InvalidOperationException($"libei would not take the desktop's descriptor (error {setup}).");
            }

            _running = true;
            _pump = new Thread(Pump) { IsBackground = true, Name = "libei" };
            _pump.Start();

            LogWaitingForDevice(_logger);
            return false;
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or PortalException)
        {
            LogCannotInject(_logger, error.Message);

            if (_ei != 0)
            {
                LibEi.Unref(_ei);
                _ei = 0;
            }

            return false;
        }
    }

    /// <summary>Drains libei, which is how the device and its capabilities become known.</summary>
    private unsafe void Pump()
    {
        var descriptor = LibEi.GetFd(_ei);
        if (descriptor < 0)
        {
            LogCannotInject(_logger, "libei gave no descriptor to watch.");
            return;
        }

        while (_running)
        {
            var poll = new LibEi.PollFd { Fd = descriptor, Events = LibEi.PollIn };
            var ready = LibEi.Poll(&poll, 1, 200);
            if (ready < 0)
            {
                if (Marshal.GetLastPInvokeError() == 4)
                {
                    continue; // EINTR
                }

                break;
            }

            if (ready == 0)
            {
                continue;
            }

            if (LibEi.Dispatch(_ei) < 0)
            {
                break;
            }

            while (LibEi.GetEvent(_ei) is var @event && @event != 0)
            {
                try
                {
                    Handle(LibEi.EventGetType(@event), LibEi.EventGetDevice(@event), LibEi.EventGetSeat(@event));
                }
                finally
                {
                    LibEi.EventUnref(@event);
                }
            }
        }
    }

    private void Handle(int type, nint device, nint seat)
    {
        switch (type)
        {
            case LibEi.EventSeatAdded:
                // Absolute rather than relative: the controller knows where on the screen the pointer should be,
                // and a stream of deltas would drift.
                LibEi.SeatBindCapabilities(seat, LibEi.CapabilityPointerAbsolute, 0);
                LibEi.SeatBindCapabilities(seat, LibEi.CapabilityButton, 0);
                LibEi.SeatBindCapabilities(seat, LibEi.CapabilityKeyboard, 0);
                LibEi.SeatBindCapabilities(seat, LibEi.CapabilityScroll, 0);
                LogSeatBound(_logger);
                break;

            case LibEi.EventDeviceAdded:
                if (device == 0)
                {
                    break;
                }

                lock (_gate)
                {
                    Remember(device);
                }

                // Described into a local first: the analyzer refuses a method call in the argument position, on the
                // grounds that it would be evaluated whether or not anything is listening.
                var capabilities = DescribeCapabilities(device);
                LogDeviceAdded(_logger, capabilities);
                break;

            case LibEi.EventDeviceResumed:
                if (device != 0)
                {
                    // The sequence number is how the compositor knows the events are continuous; starting emulating
                    // is what makes it accept them at all.
                    var sequence = Interlocked.Increment(ref _sequence);
                    var started = LibEi.DeviceStartEmulating(device, sequence);
                    LogEmulating(_logger, started);
                }

                break;

            case LibEi.EventDevicePaused:
                if (device != 0)
                {
                    LibEi.DeviceStopEmulating(device);
                }

                break;

            case LibEi.EventDeviceRemoved:
                lock (_gate)
                {
                    Forget(device);
                }

                break;

            case LibEi.EventDisconnect:
                LogCannotInject(_logger, "The desktop ended the input session.");
                break;

            default:
                break;
        }
    }

    /// <summary>Moves the pointer, and says which device it moved on.</summary>
    private nint MovePointer(string displayId, double x, double y)
    {
        var device = _pointerDevice;
        if (device == 0)
        {
            return 0;
        }

        var (desktopX, desktopY) = ToDesktop(displayId, x, y);
        LibEi.DevicePointerMotionAbsolute(device, desktopX, desktopY);
        return device;
    }

    /// <summary>Puts the pointer where the click is meant for, then clicks there.</summary>
    private nint PressButton(PointerButtonEvent button)
    {
        var pointer = MovePointer(button.DisplayId, button.X, button.Y);
        var device = _buttonDevice;
        if (device == 0)
        {
            return pointer;
        }

        LibEi.DeviceButton(device, ButtonCode(button.Button), button.IsDown);
        return device;
    }

    /// <summary>
    /// Turns a position on one screen into the desktop-wide coordinates libei wants.
    /// </summary>
    /// <remarks>
    /// The protocol normalizes against the screen the controller is looking at; the compositor only knows the
    /// desktop. In between is the scale — a screen's rectangle in device pixels is not the rectangle it occupies on
    /// the desktop, and using the wrong one puts the pointer progressively further out the more the screen is
    /// scaled. The coordinates are left in the compositor's units because that is the space its region check is in.
    /// </remarks>
    private (double X, double Y) ToDesktop(string displayId, double x, double y)
    {
        var display = FindDisplay(displayId);
        var scale = display.Scale <= 0 ? 1.0 : display.Scale;

        var left = display.Bounds.X / scale;
        var top = display.Bounds.Y / scale;
        var width = display.Bounds.Width / scale;
        var height = display.Bounds.Height / scale;

        return (left + (Math.Clamp(x, 0, 1) * width), top + (Math.Clamp(y, 0, 1) * height));
    }

    private DisplayInfo FindDisplay(string displayId)
    {
        if (DateTime.UtcNow - _cachedAt > TimeSpan.FromSeconds(5) || _cachedDisplays.Count == 0)
        {
            _cachedDisplays = _displays.GetDisplays();
            _cachedAt = DateTime.UtcNow;
        }

        DisplayInfo? primary = null;
        foreach (var display in _cachedDisplays)
        {
            if (string.Equals(display.Id, displayId, StringComparison.Ordinal))
            {
                return display;
            }

            primary ??= display.IsPrimary ? display : null;
        }

        return primary
            ?? (_cachedDisplays.Count > 0
                ? _cachedDisplays[0]
                : throw new InvalidOperationException("No screen to put the pointer on."));
    }

    /// <summary>Scrolls, on the device that can.</summary>
    private nint Scroll(PointerWheelEvent wheel)
    {
        var pointer = MovePointer(wheel.DisplayId, wheel.X, wheel.Y);
        var device = _scrollDevice;
        if (device == 0)
        {
            return pointer;
        }

        // Discrete steps, not deltas: a wheel is a wheel, and the compositor turns one step into whatever the
        // application is expecting. The protocol counts a notch as 120, which is also what Windows counts.
        LibEi.DeviceScrollDiscrete(device, wheel.DeltaX / WheelNotch, wheel.DeltaY / WheelNotch);
        return device;
    }

    /// <summary>Presses or releases a key, on the device that has a keyboard.</summary>
    private nint SendKey(KeyEvent key)
    {
        var device = _keyboardDevice;
        if (device == 0)
        {
            return 0;
        }

        if (!ScanCodeToEvdev.TryMap(key.ScanCode, key.IsExtended, out var keyCode))
        {
            LogUnmappedKey(_logger, key.ScanCode, key.IsExtended);
            return 0;
        }

        LibEi.DeviceKeyboardKey(device, (uint)keyCode, key.IsDown);
        return device;
    }

    /// <summary>
    /// Types text that has no key on any keyboard.
    /// </summary>
    /// <remarks>
    /// libei's keyboard takes evdev key codes and nothing else, so an emoji, or a character produced by an input
    /// method, cannot be expressed by pressing a key. The portal has a call for exactly this: a keysym, which is the
    /// character rather than the key. Unicode maps to one by convention — <c>0x01000000</c> with the code point in
    /// the low bits — and that is how the two halves are joined.
    /// </remarks>
    private nint SendText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var (connection, session) = _control.Current();

        foreach (var rune in text.EnumerateRunes())
        {
            // Anything that fits in a byte or two also has a keysym in the older Latin-1 range, which is what the
            // protocol means by a keysym for those.
            var keysym = rune.Value is >= 0x20 and < 0x100 ? (uint)rune.Value : (uint)(0x01000000 | rune.Value);

            connection.NotifyKeyboardKeysymAsync(session, keysym, pressed: true).GetAwaiter().GetResult();
            connection.NotifyKeyboardKeysymAsync(session, keysym, pressed: false).GetAwaiter().GetResult();
        }

        // Sent over the bus rather than through libei, so there is no device to frame.
        return 1;
    }

    /// <summary>Notes which capabilities a newly offered device can serve.</summary>
    private void Remember(nint device)
    {
        if (LibEi.DeviceHasCapability(device, LibEi.CapabilityPointerAbsolute))
        {
            _pointerDevice = device;
        }

        if (LibEi.DeviceHasCapability(device, LibEi.CapabilityButton))
        {
            _buttonDevice = device;
        }

        if (LibEi.DeviceHasCapability(device, LibEi.CapabilityKeyboard))
        {
            _keyboardDevice = device;
        }

        if (LibEi.DeviceHasCapability(device, LibEi.CapabilityScroll))
        {
            _scrollDevice = device;
        }
    }

    private void Forget(nint device)
    {
        if (_pointerDevice == device)
        {
            _pointerDevice = 0;
        }

        if (_buttonDevice == device)
        {
            _buttonDevice = 0;
        }

        if (_keyboardDevice == device)
        {
            _keyboardDevice = 0;
        }

        if (_scrollDevice == device)
        {
            _scrollDevice = 0;
        }
    }

    private static uint ButtonCode(PointerButton button) => button switch
    {
        PointerButton.Left => ButtonLeft,
        PointerButton.Right => ButtonRight,
        PointerButton.Middle => ButtonMiddle,
        PointerButton.X1 => ButtonSide,
        PointerButton.X2 => ButtonExtra,
        _ => throw new ArgumentOutOfRangeException(nameof(button)),
    };

    private static string DescribeCapabilities(nint device)
    {
        var capabilities = new List<string>();
        if (LibEi.DeviceHasCapability(device, LibEi.CapabilityPointerAbsolute))
        {
            capabilities.Add("pointer");
        }

        if (LibEi.DeviceHasCapability(device, LibEi.CapabilityButton))
        {
            capabilities.Add("buttons");
        }

        if (LibEi.DeviceHasCapability(device, LibEi.CapabilityKeyboard))
        {
            capabilities.Add("keyboard");
        }

        if (LibEi.DeviceHasCapability(device, LibEi.CapabilityScroll))
        {
            capabilities.Add("scroll");
        }

        return capabilities.Count == 0 ? "none" : string.Join(", ", capabilities);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Input is connected; waiting for the desktop to offer a device.")]
    private static partial void LogWaitingForDevice(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "A seat was offered; asked for pointer, buttons, keyboard and scroll.")]
    private static partial void LogSeatBound(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The desktop offered a device with: {Capabilities}.")]
    private static partial void LogDeviceAdded(ILogger logger, string capabilities);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Started emulating ({Result}).")]
    private static partial void LogEmulating(ILogger logger, int result);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Input cannot be sent: {Reason}")]
    private static partial void LogCannotInject(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Scan code {ScanCode} (extended: {Extended}) has no key here.")]
    private static partial void LogUnmappedKey(ILogger logger, int scanCode, bool extended);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Kind} is not something this platform can inject.")]
    private static partial void LogUnknownEvent(ILogger logger, string kind);
}
