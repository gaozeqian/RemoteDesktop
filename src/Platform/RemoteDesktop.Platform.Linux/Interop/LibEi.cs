using System.Runtime.InteropServices;

namespace RemoteDesktop.Platform.Linux.Interop;

/// <summary>
/// The part of libei a remote control needs, bound by hand.
/// </summary>
/// <remarks>
/// <para>
/// libei is how a Wayland session accepts synthetic input. The desktop portal hands over a descriptor with
/// <c>ConnectToEIS</c>, and this library speaks the protocol on it — so input arrives looking like a device the
/// compositor agreed to, rather than as writes to <c>/dev/uinput</c> which would be a machine-wide privilege
/// granted to the user permanently.
/// </para>
/// <para>
/// A <em>sender</em> injects. The context is not the same thing as the connection: <see cref="SetupBackendFd"/>
/// takes the portal's descriptor, and everything after that is events and calls.
/// </para>
/// </remarks>
internal static unsafe partial class LibEi
{
    internal const string Library = "libei.so.1";

    /// <summary>EI_DEVICE_CAP_POINTER_ABSOLUTE — a pointer positioned by coordinates, not by deltas.</summary>
    internal const int CapabilityPointerAbsolute = 1 << 1;

    /// <summary>EI_DEVICE_CAP_KEYBOARD.</summary>
    internal const int CapabilityKeyboard = 1 << 2;

    /// <summary>EI_DEVICE_CAP_SCROLL.</summary>
    internal const int CapabilityScroll = 1 << 4;

    /// <summary>EI_DEVICE_CAP_BUTTON.</summary>
    internal const int CapabilityButton = 1 << 5;

    // From libei's ei_event_type. Only the ones that change what can be sent are needed here; the rest arrive and
    // are ignored.
    internal const int EventConnect = 1;
    internal const int EventDisconnect = 2;
    internal const int EventSeatAdded = 3;
    internal const int EventSeatRemoved = 4;
    internal const int EventDeviceAdded = 5;
    internal const int EventDeviceRemoved = 6;
    internal const int EventDevicePaused = 7;
    internal const int EventDeviceResumed = 8;

    /// <summary>POLLIN, from poll.h.</summary>
    internal const short PollIn = 0x0001;

    /// <summary>
    /// <c>struct pollfd</c> — a descriptor, what is being waited for, and what happened.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFd
    {
        public int Fd;
        public short Events;
        public short Returned;
    }

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    internal static partial int Poll(PollFd* descriptors, nuint count, int timeoutMilliseconds);

    [LibraryImport(Library, EntryPoint = "ei_new_sender")]
    internal static partial nint NewSender(nint userData);

    [LibraryImport(Library, EntryPoint = "ei_configure_name")]
    internal static partial int ConfigureName(nint ei, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [LibraryImport(Library, EntryPoint = "ei_setup_backend_fd")]
    internal static partial int SetupBackendFd(nint ei, int fileDescriptor);

    [LibraryImport(Library, EntryPoint = "ei_get_fd")]
    internal static partial int GetFd(nint ei);

    [LibraryImport(Library, EntryPoint = "ei_dispatch")]
    internal static partial int Dispatch(nint ei);

    [LibraryImport(Library, EntryPoint = "ei_get_event")]
    internal static partial nint GetEvent(nint ei);

    [LibraryImport(Library, EntryPoint = "ei_event_unref")]
    internal static partial void EventUnref(nint @event);

    [LibraryImport(Library, EntryPoint = "ei_event_get_type")]
    internal static partial int EventGetType(nint @event);

    [LibraryImport(Library, EntryPoint = "ei_event_get_device")]
    internal static partial nint EventGetDevice(nint @event);

    [LibraryImport(Library, EntryPoint = "ei_event_get_seat")]
    internal static partial nint EventGetSeat(nint @event);

    /// <summary>
    /// Asks for capabilities on a seat.
    /// </summary>
    /// <remarks>
    /// Variadic in C and sentinel-terminated, which is why the terminator is an explicit argument here. Calling a
    /// variadic function with a fixed argument list is defined behaviour; the callee reads until the null, and one
    /// capability followed by the null is a list of one.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "ei_seat_bind_capabilities")]
    internal static partial void SeatBindCapabilities(nint seat, int capability, nint terminator);

    [LibraryImport(Library, EntryPoint = "ei_device_has_capability")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool DeviceHasCapability(nint device, int capability);

    [LibraryImport(Library, EntryPoint = "ei_device_start_emulating")]
    internal static partial int DeviceStartEmulating(nint device, uint sequence);

    [LibraryImport(Library, EntryPoint = "ei_device_stop_emulating")]
    internal static partial void DeviceStopEmulating(nint device);

    [LibraryImport(Library, EntryPoint = "ei_device_pointer_motion_absolute")]
    internal static partial void DevicePointerMotionAbsolute(nint device, double x, double y);

    [LibraryImport(Library, EntryPoint = "ei_device_button_button")]
    internal static partial void DeviceButton(nint device, uint button, [MarshalAs(UnmanagedType.U1)] bool isPress);

    [LibraryImport(Library, EntryPoint = "ei_device_scroll_discrete")]
    internal static partial void DeviceScrollDiscrete(nint device, int x, int y);

    [LibraryImport(Library, EntryPoint = "ei_device_keyboard_key")]
    internal static partial void DeviceKeyboardKey(nint device, uint keycode, [MarshalAs(UnmanagedType.U1)] bool isPress);

    [LibraryImport(Library, EntryPoint = "ei_device_frame")]
    internal static partial void DeviceFrame(nint device, ulong time);

    [LibraryImport(Library, EntryPoint = "ei_device_get_region")]
    internal static partial nint DeviceGetRegion(nint device, nuint index);

    [LibraryImport(Library, EntryPoint = "ei_region_get_x")]
    internal static partial uint RegionGetX(nint region);

    [LibraryImport(Library, EntryPoint = "ei_region_get_y")]
    internal static partial uint RegionGetY(nint region);

    [LibraryImport(Library, EntryPoint = "ei_region_get_width")]
    internal static partial uint RegionGetWidth(nint region);

    [LibraryImport(Library, EntryPoint = "ei_region_get_height")]
    internal static partial uint RegionGetHeight(nint region);

    [LibraryImport(Library, EntryPoint = "ei_now")]
    internal static partial ulong Now(nint ei);

    [LibraryImport(Library, EntryPoint = "ei_unref")]
    internal static partial void Unref(nint ei);
}
