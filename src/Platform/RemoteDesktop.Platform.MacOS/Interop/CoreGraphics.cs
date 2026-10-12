using System.Runtime.InteropServices;

namespace RemoteDesktop.Platform.MacOS.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct CGPoint
{
    public double X;
    public double Y;

    public CGPoint(double x, double y)
    {
        X = x;
        Y = y;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGSize
{
    public double Width;
    public double Height;
}

/// <summary>A rectangle in the global display coordinate space (origin top-left, in points).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CGRect
{
    public CGPoint Origin;
    public CGSize Size;

    public readonly int Width => (int)Math.Round(Size.Width);
    public readonly int Height => (int)Math.Round(Size.Height);
}

/// <summary>
/// CoreGraphics interop. Two jobs: enumerate the displays (and their pixel sizes) and post synthetic input events.
/// Both are plain C APIs, so no Objective-C helper is involved here.
/// </summary>
internal static unsafe partial class CoreGraphics
{
    public const int Success = 0;

    // ---- Displays ---------------------------------------------------------------------------------------------

    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial int CGGetOnlineDisplayList(uint maxDisplays, uint* onlineDisplays, uint* displayCount);

    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial uint CGMainDisplayID();

    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial CGRect CGDisplayBounds(uint display);

    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial nuint CGDisplayPixelsWide(uint display);

    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial nuint CGDisplayPixelsHigh(uint display);

    /// <summary>Returns a +1 <c>CGDisplayModeRef</c> the caller must release with <see cref="CGDisplayModeRelease"/>.</summary>
    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial nint CGDisplayCopyDisplayMode(uint display);

    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial void CGDisplayModeRelease(nint mode);

    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial nuint CGDisplayModeGetPixelWidth(nint mode);

    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial nuint CGDisplayModeGetPixelHeight(nint mode);

    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial nuint CGDisplayModeGetWidth(nint mode);

    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial nuint CGDisplayModeGetHeight(nint mode);

    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial double CGDisplayModeGetRefreshRate(nint mode);

    /// <summary>Whether the current process may capture this display (i.e. has been granted Screen Recording).</summary>
    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial byte CGPreflightScreenCaptureAccess();

    /// <summary>Asks the system for Screen Recording; the first call shows the permission prompt.</summary>
    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial byte CGRequestScreenCaptureAccess();

    // ---- Events -----------------------------------------------------------------------------------------------

    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial nint CGEventSourceCreate(int stateId);

    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial nint CGEventCreateMouseEvent(nint source, int mouseType, CGPoint mouseCursorPosition, int mouseButton);

    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial nint CGEventCreateScrollWheelEvent(nint source, int units, uint wheelCount, int wheel1);

    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial nint CGEventCreateScrollWheelEvent(nint source, int units, uint wheelCount, int wheel1, int wheel2);

    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial nint CGEventCreateKeyboardEvent(nint source, ushort virtualKey, byte keyDown);

    /// <summary>
    /// Replaces the event's text with an explicit UTF-16 string, which is how characters that have no key code of
    /// their own (IME output, emoji, characters outside the host's layout) get delivered.
    /// </summary>
    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static unsafe partial void CGEventKeyboardSetUnicodeString(nint @event, nuint stringLength, ushort* unicodeString);

    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial void CGEventSetFlags(nint @event, ulong flags);

    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial ulong CGEventGetFlags(nint @event);

    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial void CGEventSetIntegerValueField(nint @event, int field, long value);

    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial long CGEventGetIntegerValueField(nint @event, int field);

    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial void CGEventPost(int tap, nint @event);

    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial void CGWarpMouseCursorPosition(CGPoint newCursorPosition);

    /// <summary>Whether this process may post events into other applications (Accessibility permission).</summary>
    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial byte CGPreflightPostEventAccess();

    [LibraryImport(NativeLibraries.CoreGraphics)]
    internal static partial byte CGRequestPostEventAccess();

    /// <summary>Event tap location: the lowest level, which is what a remote-control injector wants.</summary>
    public const int HidEventTap = 0;

    public const int EventSourceStateHidSystemState = 1;

    // CGEventType
    public const int LeftMouseDown = 1;
    public const int LeftMouseUp = 2;
    public const int RightMouseDown = 3;
    public const int RightMouseUp = 4;
    public const int MouseMoved = 5;
    public const int LeftMouseDragged = 6;
    public const int RightMouseDragged = 7;
    public const int KeyDown = 10;
    public const int KeyUp = 11;
    public const int FlagsChanged = 12;
    public const int ScrollWheel = 22;
    public const int OtherMouseDown = 25;
    public const int OtherMouseUp = 26;
    public const int OtherMouseDragged = 27;

    // CGMouseButton
    public const int MouseButtonLeft = 0;
    public const int MouseButtonRight = 1;
    public const int MouseButtonCenter = 2;

    // CGScrollEventUnit
    public const int ScrollUnitPixel = 0;
    public const int ScrollUnitLine = 1;

    // CGEventField
    public const int FieldMouseEventButtonNumber = 3;
    public const int FieldMouseEventDeltaX = 4;
    public const int FieldMouseEventDeltaY = 5;
    public const int FieldKeyboardEventKeycode = 9;
    public const int FieldScrollWheelEventPointDeltaAxis1 = 96;
    public const int FieldScrollWheelEventPointDeltaAxis2 = 97;

    // CGEventFlags
    public const ulong FlagMaskAlphaShift = 0x0001_0000;
    public const ulong FlagMaskShift = 0x0002_0000;
    public const ulong FlagMaskControl = 0x0004_0000;
    public const ulong FlagMaskAlternate = 0x0008_0000;
    public const ulong FlagMaskCommand = 0x0010_0000;
    public const ulong FlagMaskSecondaryFn = 0x0080_0000;
    public const ulong FlagMaskNumericPad = 0x0020_0000;
}
